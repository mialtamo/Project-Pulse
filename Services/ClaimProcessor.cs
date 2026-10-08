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
    ApimOutageTracker outageTracker,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<ClaimProcessor> logger) : IClaimProcessor
{
    private readonly ClaimProcessorOptions _options = options.Value;
    private readonly HashSet<int> _retryHttpCodes = options.Value.GetRetryHttpStatusCodeSet();
    private readonly HashSet<string> _jsonRetryCodes = options.Value.GetJsonRetryCodeSet();
    private readonly HashSet<string> _jsonAlertCodes = options.Value.GetJsonAlertCodeSet();

    public async Task<IReadOnlyList<ClaimProcessingResult>> BufferBatchAsync(
        IReadOnlyList<JsonElement> claims,
        string reason,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var transformed = TransformClaims(claims);

        try
        {
            await QueueAllAsync(claims, transformed, reason, cancellationToken);
        }
        catch
        {
            outageTracker.RecordBufferFailure();
            throw;
        }

        var ids = claims.Select(GetCorrelationId).ToArray();
        telemetry.TrackEvent("ClaimBatchBufferedToServiceBus", new Dictionary<string, string>
        {
            ["recordCount"] = claims.Count.ToString(),
            ["reason"] = reason
        });

        logger.LogWarning(
            "Buffered {Count} RTA record(s) to Service Bus because APIM is unavailable. Reason={Reason}",
            claims.Count,
            reason);

        return BuildResults(ids, ClaimProcessingDisposition.QueuedForRetry, reason);
    }

    public async Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
        {
            return [];
        }

        var correlationIds = claims.Select(GetCorrelationId).ToArray();
        var transformedClaims = TransformClaims(claims);
        var batchId = Guid.NewGuid().ToString("N");

        using var operation = telemetry.StartOperation<Microsoft.ApplicationInsights.DataContracts.RequestTelemetry>("ClaimBatchAdjudication");
        operation.Telemetry.Properties["batchId"] = batchId;
        operation.Telemetry.Properties["recordCount"] = claims.Count.ToString();
        var stopwatch = Stopwatch.StartNew();

        if (!circuitBreaker.CanAttempt())
        {
            try
            {
                await QueueAllAsync(claims, transformedClaims, "OutboundCircuitOpen", cancellationToken);
            }
            catch
            {
                outageTracker.RecordBufferFailure();
                throw;
            }

            CompleteTelemetry(operation.Telemetry, false, "CircuitOpen", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "OutboundCircuitOpen");
        }

        var transformedArray = new JsonArray();
        foreach (var transformed in transformedClaims)
        {
            transformedArray.Add(transformed.DeepClone());
        }

        JsonNode outboundPayload = string.IsNullOrWhiteSpace(_options.ApimBatchRootProperty)
            ? transformedArray
            : new JsonObject { [_options.ApimBatchRootProperty] = transformedArray };

        if (_options.ShouldLogPollPayloads())
        {
            logger.LogInformation(
                "APIM outbound batch {BatchId} payload: {Payload}",
                batchId,
                outboundPayload.ToJsonString());
        }

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
                outageTracker.RecordApimFailure();

                if (_retryHttpCodes.Contains(statusCode))
                {
                    try
                    {
                        await QueueAllAsync(claims, transformedClaims, $"Http{statusCode}", cancellationToken);
                    }
                    catch
                    {
                        outageTracker.RecordBufferFailure();
                        throw;
                    }

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
            outageTracker.RecordApimSuccess();
            var businessCode = TryGetBusinessCode(responseJson);

            if (!string.IsNullOrWhiteSpace(businessCode) && _jsonRetryCodes.Contains(businessCode))
            {
                try
                {
                    await QueueAllAsync(claims, transformedClaims, $"JsonCode:{businessCode}", cancellationToken);
                }
                catch
                {
                    outageTracker.RecordBufferFailure();
                    throw;
                }

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
            outageTracker.RecordApimFailure();

            try
            {
                await QueueAllAsync(claims, transformedClaims, "ApimTimeout", cancellationToken);
            }
            catch
            {
                outageTracker.RecordBufferFailure();
                throw;
            }

            CompleteTelemetry(operation.Telemetry, false, "Timeout", stopwatch.ElapsedMilliseconds);
            return BuildResults(correlationIds, ClaimProcessingDisposition.QueuedForRetry, "ApimTimeout");
        }
        catch (HttpRequestException ex)
        {
            circuitBreaker.RecordFailure();
            outageTracker.RecordApimFailure();
            var statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : (int?)null;

            try
            {
                await QueueAllAsync(claims, transformedClaims, "NetworkFailure", cancellationToken);
            }
            catch
            {
                outageTracker.RecordBufferFailure();
                throw;
            }

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

    private IReadOnlyList<JsonObject> TransformClaims(IReadOnlyList<JsonElement> claims) =>
        claims.Select(transformer.Transform).ToArray();

    private string GetCorrelationId(JsonElement claim)
    {
        if (TryGetScalar(claim, _options.CorrelationIdField, out var configured))
        {
            return configured;
        }

        if (_options.IsMockApiMode())
        {
            if (TryGetScalar(claim, "uniqueId", out var mockId) ||
                TryGetScalar(claim, "UniqueId", out mockId))
            {
                return mockId;
            }
        }

        if (TryGetScalar(claim, "CORRELATION_ID", out var rtaId))
        {
            return rtaId;
        }

        throw new InvalidOperationException(
            $"Queued request JSON does not contain configured correlation ID field '{_options.CorrelationIdField}'.");
    }

    private async Task QueueAllAsync(
        IReadOnlyList<JsonElement> claims,
        IReadOnlyList<JsonObject> transformedClaims,
        string reason,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            var uniqueId = GetCorrelationId(claim);
            TryGetScalar(claim, "REQUEST_ID", out var requestId);
            TryGetScalar(claim, "IDEMPOTENCY_KEY", out var idempotencyKey);

            var envelope = new RetryEnvelope(
                uniqueId,
                string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                reason,
                DateTimeOffset.UtcNow,
                claim.GetRawText(),
                transformedClaims[i].ToJsonString());

            await retryQueue.EnqueueAsync(envelope, cancellationToken);
        }
    }

    private static bool TryGetScalar(JsonElement claim, string fieldName, out string value)
    {
        value = string.Empty;
        if (claim.ValueKind != JsonValueKind.Object ||
            !claim.TryGetProperty(fieldName, out var element))
        {
            return false;
        }

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.ToString()
        };

        return !string.IsNullOrWhiteSpace(value);
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
