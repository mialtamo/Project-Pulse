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
        if (!rtaHealth.IsHealthy || !apimHealth.IsHealthy)
        {
            telemetry.TrackEvent("ClaimPollSkippedDependencyUnhealthy", new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["rtaHealthy"] = rtaHealth.IsHealthy.ToString(),
                ["apimHealthy"] = apimHealth.IsHealthy.ToString()
            });

            logger.LogWarning(
                "Poll skipped because a dependency is unhealthy. RTA={RtaHealthy}, APIM/Backend={ApimHealthy}",
                rtaHealth.IsHealthy,
                apimHealth.IsHealthy);
            return;
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
            logger.LogInformation("No queued requests returned by RTA");
            return;
        }

        IReadOnlyList<ClaimProcessingResult> results;
        try
        {
            results = await claimProcessor.ProcessBatchAsync(claims, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected batch processing exception. Claim payloads are intentionally not logged.");
            telemetry.TrackException(ex);
            return;
        }

        var summary = results
            .GroupBy(x => x.Disposition)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        telemetry.TrackEvent("ClaimPollCompleted", summary.ToDictionary(x => x.Key, x => x.Value.ToString()));
        telemetry.TrackMetric("ClaimBatchSize", claims.Count);

        logger.LogInformation(
            "Poll completed. Batched={Batched}, Completed={Completed}, Retry={Retry}, Alerted={Alerted}, WritebackFailed={WritebackFailed}",
            claims.Count,
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Completed), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.QueuedForRetry), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Alerted), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.FailedWriteback), 0));
    }
}
