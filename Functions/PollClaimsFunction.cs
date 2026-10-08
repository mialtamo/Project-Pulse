using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ProjectPulse.Processor.Functions;

public sealed class PollClaimsFunction(
    IHealthGate healthGate,
    IAppApiClient appApiClient,
    IClaimProcessor claimProcessor,
    ApimOutageTracker outageTracker,
    TelemetryClient telemetry,
    ILogger<PollClaimsFunction> logger)
{
    [Function("PollClaims")]
    public async Task Run(
        [TimerTrigger("%POLL_SCHEDULE%", UseMonitor = true)] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["PollRunId"] = runId });

        logger.LogInformation("RTA queued request poll started. Schedule status: {ScheduleStatus}", timerInfo.ScheduleStatus);

        var (rtaHealth, apimHealth) = await healthGate.CheckAsync(cancellationToken);

        if (!rtaHealth.IsHealthy)
        {
            telemetry.TrackEvent("ClaimPollSkippedRtaUnhealthy", new Dictionary<string, string>
            {
                ["runId"] = runId
            });

            logger.LogWarning("Poll skipped because RTA API/DB health is unhealthy");
            return;
        }

        var outageDecision = outageTracker.Evaluate(apimHealth.IsHealthy);

        if (outageDecision.OutageStarted)
        {
            logger.LogWarning(
                "APIM outage detected. Mode decision={Action}. Temporary buffering window started",
                outageDecision.Action);
        }

        if (outageDecision.Recovered)
        {
            logger.LogInformation("APIM recovered. Normal RTA polling and APIM delivery resumed");
            telemetry.TrackEvent("ApimRecovered");
        }

        if (outageDecision.Action == ApimOutageAction.StopPolling)
        {
            telemetry.TrackEvent("ClaimPollSuspendedForApimOutage", new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["outageSeconds"] = Math.Round(outageDecision.OutageSeconds).ToString(),
                ["reason"] = outageDecision.Reason
            });

            logger.LogWarning(
                "RTA polling suspended. APIM outage duration={OutageSeconds:F0}s Reason={Reason}",
                outageDecision.OutageSeconds,
                outageDecision.Reason);
            return;
        }

        if (outageDecision.Action == ApimOutageAction.BufferToServiceBus)
        {
            logger.LogWarning(
                "APIM unhealthy for {OutageSeconds:F0}s. RTA polling will continue temporarily and records will be buffered to Service Bus",
                outageDecision.OutageSeconds);
        }

        IReadOnlyList<System.Text.Json.JsonElement> claims;
        try
        {
            claims = await appApiClient.GetPendingClaimsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "RTA queued polling failed before records could be processed");
            telemetry.TrackException(ex);
            return;
        }

        if (claims.Count == 0)
        {
            logger.LogInformation("No queued requests returned by RTA/App API");
            return;
        }

        logger.LogInformation(
            "RTA/App API returned {Count} queued record(s). DeliveryMode={DeliveryMode}",
            claims.Count,
            outageDecision.Action == ApimOutageAction.BufferToServiceBus ? "ServiceBusBuffer" : "APIM");

        IReadOnlyList<ClaimProcessingResult> results;
        try
        {
            results = outageDecision.Action == ApimOutageAction.BufferToServiceBus
                ? await claimProcessor.BufferBatchAsync(claims, outageDecision.Reason, cancellationToken)
                : await claimProcessor.ProcessBatchAsync(claims, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Batch processing failed. If APIM is unavailable and Service Bus buffering also failed, polling will remain suspended until APIM recovers");
            telemetry.TrackException(ex);
            return;
        }

        var summary = results
            .GroupBy(x => x.Disposition)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        telemetry.TrackEvent("ClaimPollCompleted", summary.ToDictionary(x => x.Key, x => x.Value.ToString()));
        telemetry.TrackMetric("ClaimBatchSize", claims.Count);

        logger.LogInformation(
            "Poll completed. Batched={Batched}, Completed={Completed}, RetryBuffered={Retry}, Alerted={Alerted}, WritebackFailed={WritebackFailed}",
            claims.Count,
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Completed), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.QueuedForRetry), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Alerted), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.FailedWriteback), 0));
    }
}
