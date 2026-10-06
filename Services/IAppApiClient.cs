using System.Text.Json;

namespace ProjectPulse.Processor.Services;

public interface IAppApiClient
{
    Task<IReadOnlyList<JsonElement>> GetPendingClaimsAsync(CancellationToken cancellationToken);
    Task<bool> WriteResultAsync(string uniqueId, string responseJson, CancellationToken cancellationToken);
}
