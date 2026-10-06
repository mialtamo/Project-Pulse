namespace ProjectPulse.Processor.Services;

public interface IRetryQueue
{
    Task EnqueueAsync(string uniqueId, string reason, CancellationToken cancellationToken);
}
