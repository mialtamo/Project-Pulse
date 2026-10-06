using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class HealthGate(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<HealthGate> logger) : IHealthGate
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public async Task<(HealthResult AppApi, HealthResult Apim)> CheckAsync(CancellationToken cancellationToken)
    {
        var appApiTask = ProbeAsync(
            "AppApi",
            httpClientFactory.CreateClient("AppApi"),
            _options.AppApiHealthPath,
            _options.AppApiScope,
            cancellationToken);

        var apimTask = ProbeAsync(
            "ApimAndBackend",
            httpClientFactory.CreateClient("Apim"),
            _options.ApimHealthPath,
            _options.ApimScope,
            cancellationToken);

        await Task.WhenAll(appApiTask, apimTask);
        return (await appApiTask, await apimTask);
    }

    private async Task<HealthResult> ProbeAsync(
        string dependency,
        HttpClient client,
        string path,
        string scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Report(new HealthResult(true, dependency, Detail: "Health probe disabled"));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            await authHeaderProvider.ApplyBearerTokenAsync(request, scope, cancellationToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var result = new HealthResult(
                response.IsSuccessStatusCode,
                dependency,
                (int)response.StatusCode,
                response.ReasonPhrase);

            return Report(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Health check failed for {Dependency}", dependency);
            return Report(new HealthResult(false, dependency, Detail: ex.Message));
        }
    }

    private HealthResult Report(HealthResult result)
    {
        telemetry.TrackMetric($"DependencyHealth.{result.Dependency}", result.IsHealthy ? 1 : 0);
        telemetry.TrackEvent(
            "DependencyHealth",
            new Dictionary<string, string>
            {
                ["dependency"] = result.Dependency,
                ["healthy"] = result.IsHealthy.ToString(),
                ["statusCode"] = result.StatusCode?.ToString() ?? string.Empty,
                ["detail"] = result.Detail ?? string.Empty
            });

        if (!result.IsHealthy)
        {
            logger.LogWarning("Dependency {Dependency} is unhealthy. Status={StatusCode} Detail={Detail}",
                result.Dependency, result.StatusCode, result.Detail);
        }

        return result;
    }
}
