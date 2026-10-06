using System.Net;
using System.Text;
using System.Text.Json;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class AppApiClient(
    IHttpClientFactory httpClientFactory,
    IAuthHeaderProvider authHeaderProvider,
    IOptions<ClaimProcessorOptions> options,
    TelemetryClient telemetry,
    ILogger<AppApiClient> logger) : IAppApiClient
{
    private readonly ClaimProcessorOptions _options = options.Value;

    public async Task<IReadOnlyList<JsonElement>> GetPendingClaimsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
        var method = new HttpMethod(_options.AppApiPollMethod.Trim().ToUpperInvariant());
        using var request = new HttpRequestMessage(method, _options.AppApiPendingClaimsPath);
        await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

        if (method == HttpMethod.Post || method == HttpMethod.Put || method.Method == "PATCH")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            telemetry.TrackEvent("AppApiPollFailed", new Dictionary<string, string>
            {
                ["statusCode"] = ((int)response.StatusCode).ToString()
            });
            throw new HttpRequestException(
                $"App API polling endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var records = new List<JsonElement>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            records.AddRange(root.EnumerateArray().Select(item => item.Clone()));
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 !string.IsNullOrWhiteSpace(_options.AppApiRecordsField) &&
                 root.TryGetProperty(_options.AppApiRecordsField, out var recordField))
        {
            if (recordField.ValueKind == JsonValueKind.Array)
            {
                records.AddRange(recordField.EnumerateArray().Select(item => item.Clone()));
            }
            else if (recordField.ValueKind == JsonValueKind.Object)
            {
                records.Add(recordField.Clone());
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            records.Add(root.Clone());
        }

        telemetry.TrackMetric("ClaimsReturnedFromAppApi", records.Count);
        logger.LogInformation("App API returned {Count} independent claim record(s)", records.Count);
        return records;
    }

    public async Task<bool> WriteResultAsync(string uniqueId, string responseJson, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("AppApi");
        var path = _options.AppApiResultPathTemplate.Replace(
            "{uniqueId}",
            Uri.EscapeDataString(uniqueId),
            StringComparison.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= Math.Max(1, _options.ResultWriteMaxAttempts); attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("X-Claim-UniqueId", uniqueId);
                await authHeaderProvider.ApplyBearerTokenAsync(request, _options.AppApiScope, cancellationToken);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.LogWarning(
                    "Result writeback failed for claim {UniqueId}. Attempt {Attempt}/{MaxAttempts}, status {StatusCode}",
                    uniqueId, attempt, _options.ResultWriteMaxAttempts, (int)response.StatusCode);

                if (!IsTransient(response.StatusCode))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex,
                    "Result writeback exception for claim {UniqueId}. Attempt {Attempt}/{MaxAttempts}",
                    uniqueId, attempt, _options.ResultWriteMaxAttempts);
            }

            if (attempt < _options.ResultWriteMaxAttempts)
            {
                await Task.Delay(_options.ResultWriteRetryDelayMilliseconds * attempt, cancellationToken);
            }
        }

        telemetry.TrackEvent("ClaimResultWritebackFailed", new Dictionary<string, string>
        {
            ["uniqueId"] = uniqueId
        });
        return false;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;
}
