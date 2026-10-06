using System.Text.Json;
using ProjectPulse.Processor.Models;

namespace ProjectPulse.Processor.Services;

public interface IClaimProcessor
{
    Task<ClaimProcessingResult> ProcessAsync(JsonElement claim, CancellationToken cancellationToken);
}
