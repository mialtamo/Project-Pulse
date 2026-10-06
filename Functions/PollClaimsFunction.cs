using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using ProjectPulse.Processor.Services;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Functions;

public sealed class PollClaimsFunction(
    IHealthGate healthGate,
    IAppApiClient appApiClient,
    IClaimProcessor claimProcessor,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<PollClaimsFunction> logger)
{
    private readonly ClaimProcessorOptions _options = options.Value;

    [Function("PollClaims")]
    public async Task Run(
        [TimerTrigger("%POLL_SCHEDULE%", UseMonitor = true)] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["PollRunId"] = runId });

        logger.LogInformation("Claim poll started. Schedule status: {ScheduleStatus}", timerInfo.ScheduleStatus);

        var (appApiHealth, apimHealth) = await healthGate.CheckAsync(cancellationToken);
        if (!appApiHealth.IsHealthy || !apimHealth.IsHealthy)
        {
            telemetry.TrackEvent("ClaimPollSkippedDependencyUnhealthy", new Dictionary<string, string>
            {
                ["runId"] = runId,
                ["appApiHealthy"] = appApiHealth.IsHealthy.ToString(),
                ["apimHealthy"] = apimHealth.IsHealthy.ToString()
            });

            logger.LogWarning(
                "Claim poll skipped because a dependency is unhealthy. AppApi={AppApiHealthy}, APIM/Backend={ApimHealthy}",
                appApiHealth.IsHealthy,
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
            logger.LogError(ex, "Claim polling failed before records could be processed");
            telemetry.TrackException(ex);
            return;
        }

        if (claims.Count == 0)
        {
            logger.LogInformation("No claims returned by the App API");
            return;
        }

        var results = new System.Collections.Concurrent.ConcurrentBag<ClaimProcessingResult>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentRequests),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(claims, parallelOptions, async (claim, ct) =>
        {
            try
            {
                results.Add(await claimProcessor.ProcessAsync(claim, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected claim processing exception. Claim payload is intentionally not logged.");
                telemetry.TrackException(ex);
            }
        });

        var summary = results
            .GroupBy(x => x.Disposition)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        telemetry.TrackEvent("ClaimPollCompleted", summary.ToDictionary(x => x.Key, x => x.Value.ToString()));
        logger.LogInformation(
            "Claim poll completed. Returned={Returned}, Completed={Completed}, Retry={Retry}, Alerted={Alerted}, WritebackFailed={WritebackFailed}",
            claims.Count,
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Completed), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.QueuedForRetry), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.Alerted), 0),
            summary.GetValueOrDefault(nameof(ClaimProcessingDisposition.FailedWriteback), 0));
    }
}
