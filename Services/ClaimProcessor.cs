using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ClaimProcessor(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IAppApiClient appApiClient,
    IRetryQueue retryQueue,
    OutboundCircuitBreaker circuitBreaker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly HashSet<int> _retryHttpCodes = options.Value.GetRetryHttpStatusCodeSet();
    private readonly HashSet<string> _jsonRetryCodes = options.Value.GetJsonRetryCodeSet();
    private readonly HashSet<string> _jsonAlertCodes = options.Value.GetJsonAlertCodeSet();

    public async Task<ClaimProcessingResult> ProcessAsync(JsonElement claim, CancellationToken cancellationToken)
    {
        var uniqueId = GetUniqueId(claim);
        using var operation = telemetry.StartOperation<Microsoft.ApplicationInsights.DataContracts.RequestTelemetry>("ClaimAdjudication");
        operation.Telemetry.Properties["uniqueId"] = uniqueId;
        var stopwatch = Stopwatch.StartNew();

        if (!circuitBreaker.CanAttempt())
        {
            await retryQueue.EnqueueAsync(uniqueId, "OutboundCircuitOpen", cancellationToken);
            CompleteTelemetry(operation.Telemetry, false, "CircuitOpen", stopwatch.ElapsedMilliseconds);
            return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.QueuedForRetry, "OutboundCircuitOpen");
        }

        var client = httpClientFactory.CreateClient("Apim");
        var claimJson = claim.GetRawText();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApimAdjudicationPath)
        {
            Content = new StringContent(claimJson, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", uniqueId);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", uniqueId);
        await authHeaderProvider.ApplyBearerTokenAsync(request, _options.ApimScope, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ApimRequestTimeoutSeconds));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                circuitBreaker.RecordFailure();

                if (_retryHttpCodes.Contains(statusCode))
                {
                    await retryQueue.EnqueueAsync(uniqueId, $"Http{statusCode}", cancellationToken);
                    CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                    return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.QueuedForRetry, $"Http{statusCode}", statusCode);
                }

                logger.LogError("Claim {UniqueId} received non-retry HTTP status {StatusCode}", uniqueId, statusCode);
                telemetry.TrackEvent("ClaimNonRetryHttpFailure", new Dictionary<string, string>
                {
                    ["uniqueId"] = uniqueId,
                    ["statusCode"] = statusCode.ToString()
                });
                CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.Alerted, $"Http{statusCode}", statusCode);
            }

            circuitBreaker.RecordSuccess();
            var businessCode = TryGetBusinessCode(responseJson);

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonRetryCodes.Contains(businessCode))
            {
                await retryQueue.EnqueueAsync(uniqueId, $"JsonCode:{businessCode}", cancellationToken);
                CompleteTelemetry(operation.Telemetry, false, $"JSON {businessCode}", stopwatch.ElapsedMilliseconds);
                return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.QueuedForRetry, "JsonRetryCode", statusCode, businessCode);
            }

            var writebackSucceeded = await appApiClient.WriteResultAsync(uniqueId, responseJson, cancellationToken);
            if (!writebackSucceeded)
            {
                logger.LogError(
                    "Claim {UniqueId} was adjudicated but result writeback failed. The claim will NOT be automatically resubmitted to avoid duplicate adjudication.",
                    uniqueId);
                CompleteTelemetry(operation.Telemetry, false, "WritebackFailed", stopwatch.ElapsedMilliseconds);
                return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.FailedWriteback, "ResultWritebackFailed", statusCode, businessCode);
            }

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonAlertCodes.Contains(businessCode))
            {
                telemetry.TrackEvent("ClaimBusinessAlert", new Dictionary<string, string>
                {
                    ["uniqueId"] = uniqueId,
                    ["businessCode"] = businessCode
                });
                logger.LogWarning("Claim {UniqueId} returned configured JSON alert code {BusinessCode}", uniqueId, businessCode);
                CompleteTelemetry(operation.Telemetry, true, $"JSON alert {businessCode}", stopwatch.ElapsedMilliseconds);
                return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.Alerted, "JsonAlertCode", statusCode, businessCode);
            }

            CompleteTelemetry(operation.Telemetry, true, "Completed", stopwatch.ElapsedMilliseconds);
            return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.Completed, "Completed", statusCode, businessCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuitBreaker.RecordFailure();
            await retryQueue.EnqueueAsync(uniqueId, "ApimTimeout", cancellationToken);
            CompleteTelemetry(operation.Telemetry, false, "Timeout", stopwatch.ElapsedMilliseconds);
            return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.QueuedForRetry, "ApimTimeout");
        }
        catch (HttpRequestException ex)
        {
            circuitBreaker.RecordFailure();
            var statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int?)null;
            await retryQueue.EnqueueAsync(uniqueId, "NetworkFailure", cancellationToken);
            logger.LogWarning(ex, "Network failure while processing claim {UniqueId}", uniqueId);
            CompleteTelemetry(operation.Telemetry, false, "NetworkFailure", stopwatch.ElapsedMilliseconds);
            return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.QueuedForRetry, "NetworkFailure", statusCode);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON response while processing claim {UniqueId}", uniqueId);
            telemetry.TrackEvent("ClaimInvalidJsonResponse", new Dictionary<string, string> { ["uniqueId"] = uniqueId });
            CompleteTelemetry(operation.Telemetry, false, "InvalidJson", stopwatch.ElapsedMilliseconds);
            return new ClaimProcessingResult(uniqueId, ClaimProcessingDisposition.Alerted, "InvalidJsonResponse");
        }
    }

    private string GetUniqueId(JsonElement claim)
    {
        if (claim.ValueKind != JsonValueKind.Object ||
            !claim.TryGetProperty(_options.UniqueIdField, out var value))
        {
            throw new InvalidOperationException($"Claim JSON does not contain configured unique ID field '{_options.UniqueIdField}'.");
        }

        var uniqueId = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            throw new InvalidOperationException($"Claim unique ID field '{_options.UniqueIdField}' is empty.");
        }

        return uniqueId;
    }

    private string? TryGetBusinessCode(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(_options.JsonResponseCodeField))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(_options.JsonResponseCodeField, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()
        };
    }

    private static void CompleteTelemetry(
        Microsoft.ApplicationInsights.DataContracts.RequestTelemetry request,
        bool success,
        string responseCode,
        long elapsedMilliseconds)
    {
        request.Success = success;
        request.ResponseCode = responseCode;
        request.Duration = TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }
}
