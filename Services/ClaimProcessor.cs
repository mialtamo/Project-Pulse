using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    PayloadTransformer transformer,
    OutboundCircuitBreaker circuitBreaker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly HashSet<int> _retryHttpCodes = options.Value.GetRetryHttpStatusCodeSet();
    private readonly HashSet<string> _jsonRetryCodes = options.Value.GetJsonRetryCodeSet();
    private readonly HashSet<string> _jsonAlertCodes = options.Value.GetJsonAlertCodeSet();

    public async Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var correlationIds = claims.Select(GetCorrelationId).ToArray();
        var batchId = Guid.NewGuid().ToString("N");
        using var operation = telemetry.StartOperation<Microsoft.ApplicationInsights.DataContracts.RequestTelemetry>("ClaimBatchAdjudication");
        operation.Telemetry.Properties["batchId"] = batchId;
        operation.Telemetry.Properties["recordCount"] = claims.Count.ToString();
        var stopwatch = Stopwatch.StartNew();

        if (!circuitBreaker.CanAttempt())
        {
            await QueueAllAsync(correlationIds, "OutboundCircuitOpen", cancellationToken);
            CompleteTelemetry(operation.Telemetry, false, "CircuitOpen", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "OutboundCircuitOpen");
        }

        var transformed = new JsonArray();
        foreach (var claim in claims)
        {
            transformed.Add(transformer.Transform(claim));
        }

        JsonNode outboundPayload = string.IsNullOrWhiteSpace(_options.ApimBatchRootProperty)
            ? transformed
            : new JsonObject { [_options.ApimBatchRootProperty] = transformed };

        var client = httpClientFactory.CreateClient("Apim");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApimAdjudicationPath)
        {
            Content = new StringContent(outboundPayload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("X-Correlation-ID", batchId);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", batchId);
        request.Headers.TryAddWithoutValidation("X-Pulse-Record-Count", claims.Count.ToString());
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
                    await QueueAllAsync(correlationIds, $"Http{statusCode}", cancellationToken);
                    CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                    return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, $"Http{statusCode}", statusCode);
                }

                logger.LogError(
                    "APIM batch {BatchId} with {Count} records received non-retry HTTP status {StatusCode}",
                    batchId,
                    claims.Count,
                    statusCode);

                CompleteTelemetry(operation.Telemetry, false, $"HTTP {statusCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, $"Http{statusCode}", statusCode);
            }

            circuitBreaker.RecordSuccess();
            var businessCode = TryGetBusinessCode(responseJson);

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonRetryCodes.Contains(businessCode))
            {
                await QueueAllAsync(correlationIds, $"JsonCode:{businessCode}", cancellationToken);
                CompleteTelemetry(operation.Telemetry, false, $"JSON {businessCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "JsonRetryCode", statusCode, businessCode);
            }

            if (_options.RtaStatusWritebackEnabled)
            {
                var writebackResults = await WriteBatchResultsAsync(responseJson, correlationIds, cancellationToken);
                if (!writebackResults)
                {
                    CompleteTelemetry(operation.Telemetry, false, "WritebackFailed", stopwatch.ElapsedMilliseconds);
                    return BuildResults(correlationIds, ClaimProcessingDisposition.FailedWriteback, "ResultWritebackFailed", statusCode, businessCode);
                }
            }
            else
            {
                telemetry.TrackEvent("RtaStatusWritebackSkipped", new Dictionary<string, string>
                {
                    ["batchId"] = batchId,
                    ["recordCount"] = claims.Count.ToString()
                });
            }

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonAlertCodes.Contains(businessCode))
            {
                telemetry.TrackEvent("ClaimBatchBusinessAlert", new Dictionary<string, string>
                {
                    ["batchId"] = batchId,
                    ["businessCode"] = businessCode,
                    ["recordCount"] = claims.Count.ToString()
                });

                CompleteTelemetry(operation.Telemetry, true, $"JSON alert {businessCode}", stopwatch.ElapsedMilliseconds);
                return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, "JsonAlertCode", statusCode, businessCode);
            }

            CompleteTelemetry(operation.Telemetry, true, "Completed", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.Completed, "BatchCompleted", statusCode, businessCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            circuitBreaker.RecordFailure();
            await QueueAllAsync(correlationIds, "ApimTimeout", cancellationToken);
            CompleteTelemetry(operation.Telemetry, false, "Timeout", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "ApimTimeout");
        }
        catch (HttpRequestException ex)
        {
            circuitBreaker.RecordFailure();
            var statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int?)null;
            await QueueAllAsync(correlationIds, "NetworkFailure", cancellationToken);
            logger.LogWarning(ex, "Network failure while processing APIM batch {BatchId}", batchId);
            CompleteTelemetry(operation.Telemetry, false, "NetworkFailure", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "NetworkFailure", statusCode);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON response while processing APIM batch {BatchId}", batchId);
            telemetry.TrackException(ex);
            CompleteTelemetry(operation.Telemetry, false, "InvalidJson", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.Alerted, "InvalidJsonResponse");
        }
    }

    private string GetCorrelationId(JsonElement claim)
    {
        if (claim.ValueKind != JsonValueKind.Object ||
            !claim.TryGetProperty(_options.CorrelationIdField, out var value))
        {
            throw new InvalidOperationException(
                $"Queued request JSON does not contain configured correlation ID field '{_options.CorrelationIdField}'.");
        }

        var correlationId = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new InvalidOperationException(
                $"Queued request correlation ID field '{_options.CorrelationIdField}' is empty.");
        }

        return correlationId;
    }

    private async Task QueueAllAsync(
        IEnumerable<string> correlationIds,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var correlationId in correlationIds)
        {
            await retryQueue.EnqueueAsync(correlationId, reason, cancellationToken);
        }
    }

    private async Task<bool> WriteBatchResultsAsync(
        string responseJson,
        IReadOnlyList<string> correlationIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            logger.LogWarning("RTA writeback is enabled but APIM returned an empty response body.");
            return false;
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning(
                "RTA writeback is enabled but APIM response contract is not yet an array. Writeback skipped until the final APIM contract is known.");
            return false;
        }

        var success = true;
        foreach (var item in root.EnumerateArray())
        {
            if (!TryGetResponseCorrelationId(item, out var correlationId))
            {
                logger.LogWarning("APIM response item did not contain a correlation ID. Writeback skipped for that item.");
                success = false;
                continue;
            }

            success &= await appApiClient.WriteStatusAsync(
                correlationId,
                item.GetRawText(),
                cancellationToken);
        }

        if (root.GetArrayLength() != correlationIds.Count)
        {
            logger.LogWarning(
                "APIM response item count {ResponseCount} did not match submitted batch count {SubmittedCount}",
                root.GetArrayLength(),
                correlationIds.Count);
        }

        return success;
    }

    private static bool TryGetResponseCorrelationId(JsonElement item, out string correlationId)
    {
        correlationId = string.Empty;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!item.TryGetProperty("CORRELATION_ID", out var value) &&
            !item.TryGetProperty("correlationId", out value))
        {
            return false;
        }

        correlationId = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();

        return !string.IsNullOrWhiteSpace(correlationId);
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

    private static IReadOnlyList<ClaimProcessingResult> BuildResults(
        IEnumerable<string> correlationIds,
        ClaimProcessingDisposition disposition,
        string reason,
        int? httpStatusCode = null,
        string? businessCode = null) =>
        correlationIds
            .Select(id => new ClaimProcessingResult(id, disposition, reason, httpStatusCode, businessCode))
            .ToArray();

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
