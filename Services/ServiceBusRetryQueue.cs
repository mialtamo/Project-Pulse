using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ProjectPulse.Processor.Models;
using ProjectPulse.Processor.Options;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProjectPulse.Processor.Services;

public sealed class ServiceBusRetryQueue : IRetryQueue, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ClaimProcessorOptions _options;
    private readonly TelemetryClient _telemetry;
    private readonly ILogger<ServiceBusRetryQueue> _logger;

    public ServiceBusRetryQueue(
        ServiceBusClient client,
        IOptions<ClaimProcessorOptions> options,
        TelemetryClient telemetry,
        ILogger<ServiceBusRetryQueue> logger)
    {
        _options = options.Value;
        _telemetry = telemetry;
        _logger = logger;
        _sender = client.CreateSender(_options.ServiceBusQueueName);
    }

    public async Task EnqueueAsync(string uniqueId, string reason, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new RetryEnvelope(uniqueId));
        var message = new ServiceBusMessage(payload)
        {
            ContentType = "application/json",
            MessageId = uniqueId,
            CorrelationId = uniqueId,
            TimeToLive = TimeSpan.FromMinutes(_options.ServiceBusMessageTtlMinutes),
            Subject = "ClaimRetry"
        };

        message.ApplicationProperties["reason"] = reason;

        await _sender.SendMessageAsync(message, cancellationToken);

        _telemetry.TrackEvent("ClaimQueuedForRetry", new Dictionary<string, string>
        {
            ["uniqueId"] = uniqueId,
            ["reason"] = reason,
            ["ttlMinutes"] = _options.ServiceBusMessageTtlMinutes.ToString()
        });

        _logger.LogWarning("Claim {UniqueId} queued for retry. Reason={Reason}", uniqueId, reason);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
