using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ProjectPulse.Processor.Functions;

public sealed class PulseHealthFunction
{
    private readonly HttpClient _httpClient = new();

    [Function("PulseHealth")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pulse-health")]
        HttpRequestData request)
    {
        var appApi = await CheckHttpAsync(
            Environment.GetEnvironmentVariable("AppApiBaseUrl"),
            Environment.GetEnvironmentVariable("RtaHealthPath") ??
            Environment.GetEnvironmentVariable("AppApiHealthPath") ??
            "/health");

        var apim = await CheckHttpAsync(
            Environment.GetEnvironmentVariable("ApimBaseUrl"),
            Environment.GetEnvironmentVariable("ApimHealthPath") ?? "/health");

        var serviceBus = await CheckServiceBusAsync(
            Environment.GetEnvironmentVariable("ServiceBusFullyQualifiedNamespace"));

        var result = new
        {
            appApi,
            apim,
            serviceBus,
            checkedAt = DateTimeOffset.UtcNow
        };

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        await response.WriteStringAsync(JsonSerializer.Serialize(result));

        return response;
    }

    private async Task<bool> CheckHttpAsync(string? baseUrl, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetAsync(
                $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}",
                cts.Token);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckServiceBusAsync(string? host)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            using var client = new TcpClient();

            using var cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(host, 5671, cts.Token);

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
