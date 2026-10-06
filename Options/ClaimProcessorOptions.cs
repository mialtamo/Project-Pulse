using System.ComponentModel.DataAnnotations;

namespace ProjectPulse.Processor.Options;

public sealed class ClaimProcessorOptions
{
    [Required]
    public string AppApiBaseUrl { get; init; } = string.Empty;

    public string AppApiHealthPath { get; init; } = "/health";
    public string AppApiPendingClaimsPath { get; init; } = "/claims/pending";
    public string AppApiResultPathTemplate { get; init; } = "/claims/{uniqueId}/result";
    public string AppApiPollMethod { get; init; } = "POST";
    public string AppApiRecordsField { get; init; } = "records";
    public int AppApiTimeoutSeconds { get; init; } = 10;
    public string AppApiScope { get; init; } = string.Empty;

    [Required]
    public string ApimBaseUrl { get; init; } = string.Empty;

    public string ApimHealthPath { get; init; } = "/health";
    public string ApimAdjudicationPath { get; init; } = "/claims/adjudicate";
    public int ApimRequestTimeoutSeconds { get; init; } = 15;
    public string ApimScope { get; init; } = string.Empty;

    public string UniqueIdField { get; init; } = "UniqueId";
    public int MaxConcurrentRequests { get; init; } = 50;

    public string RetryHttpStatusCodes { get; init; } = "404,408,429,500,502,503,504";
    public string JsonResponseCodeField { get; init; } = "ResponseCode";
    public string JsonRetryCodes { get; init; } = string.Empty;
    public string JsonAlertCodes { get; init; } = "419";

    [Required]
    public string ServiceBusFullyQualifiedNamespace { get; init; } = string.Empty;

    public string ServiceBusQueueName { get; init; } = "claim-retry";
    public int ServiceBusMessageTtlMinutes { get; init; } = 10;

    public int CircuitFailureThreshold { get; init; } = 5;
    public int CircuitOpenSeconds { get; init; } = 30;

    public int ResultWriteMaxAttempts { get; init; } = 3;
    public int ResultWriteRetryDelayMilliseconds { get; init; } = 500;

    public HashSet<int> GetRetryHttpStatusCodeSet() => ParseIntSet(RetryHttpStatusCodes);
    public HashSet<string> GetJsonRetryCodeSet() => ParseStringSet(JsonRetryCodes);
    public HashSet<string> GetJsonAlertCodeSet() => ParseStringSet(JsonAlertCodes);

    private static HashSet<int> ParseIntSet(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var parsed) ? parsed : -1)
            .Where(x => x >= 0)
            .ToHashSet();

    private static HashSet<string> ParseStringSet(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
