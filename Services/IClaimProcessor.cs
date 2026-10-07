using System.Text.Json;
using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IClaimProcessor
{
    Task<IReadOnlyList<ClaimProcessingResult>> ProcessBatchAsync(
        IReadOnlyList<JsonElement> claims,
        CancellationToken cancellationToken);
}
