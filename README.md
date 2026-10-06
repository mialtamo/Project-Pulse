# Project Pulse

**Project Pulse** is a .NET 10 isolated Azure Functions application for health-aware claim adjudication orchestration. It polls an internal App API, sends each returned claim independently through APIM to a third-party adjudication service, receives the adjudication response over the same HTTP request, and writes the result back through the internal App API.


## Project naming

- GitHub repository: `project-pulse`
- .NET project: `ProjectPulse.Processor.csproj`
- Root namespace: `ProjectPulse.Processor`
- Primary Azure Function: `PollClaims`
- Recommended Azure Function App: `func-pulse-processor-<env>`
- Recommended retry Function App: `func-pulse-retry-<env>`
- Recommended Service Bus queue: `pulse-claim-retry`

## Processing model

One API poll may return zero, one, or many records. Returned records are never treated as one business batch. Every record is handled as its own transaction:

1. Timer fires according to `POLL_SCHEDULE`.
2. Function probes the internal App API health endpoint.
3. Function probes the APIM/backend health endpoint.
4. If either dependency is unhealthy, the poll is skipped. The timer remains alive and checks again on the next schedule.
5. Function calls the internal App API polling endpoint.
6. Each returned record is processed independently with bounded parallelism.
7. One record produces one APIM request and one third-party response.
8. Successful adjudication JSON is written back through the internal App API.
9. Configured HTTP/network/timeout failures enqueue only `{ "UniqueId": "..." }` to Service Bus.
10. Service Bus messages have a 10-minute TTL by default.

## Important behavior

The timer trigger executes once per schedule across the Function App. Scaling the Flex Consumption plan does not cause multiple timer instances to poll the API at the same time. `MaxConcurrentRequests` controls independent 1:1 processing inside that invocation.

If sustained throughput later requires horizontal distribution across many Function instances, the existing `UNIQUEID` queue can be promoted from retry-only to the normal dispatch mechanism without changing the App API or APIM contracts.

## Poll frequency

`POLL_SCHEDULE` is an Azure Functions NCRONTAB expression.

Examples:

- Every 60 seconds: `0 * * * * *`
- Every 10 seconds: `*/10 * * * * *`
- Every 5 seconds: `*/5 * * * * *`
- Every second: `* * * * * *`

Use the fastest interval only after load testing the internal API and downstream system.

## App API response formats

The poll endpoint can return any of these forms.

Root array:

```json
[
  { "UniqueId": "A100", "claim": {} },
  { "UniqueId": "A101", "claim": {} }
]
```

Wrapped collection using `AppApiRecordsField=records`:

```json
{
  "records": [
    { "UniqueId": "A100", "claim": {} },
    { "UniqueId": "A101", "claim": {} }
  ]
}
```

Single record:

```json
{ "UniqueId": "A100", "claim": {} }
```

Every record is processed separately.

## Required Azure application settings

| Setting | Example | Purpose |
| --- | --- | --- |
| `POLL_SCHEDULE` | `0 * * * * *` | Poll cadence |
| `AppApiBaseUrl` | `https://internal-api.contoso.local` | Internal App API FQDN |
| `AppApiHealthPath` | `/health` | Must represent App API and SQL/SP health |
| `AppApiPendingClaimsPath` | `/claims/pending` | Endpoint that invokes the stored procedure |
| `AppApiResultPathTemplate` | `/claims/{uniqueId}/result` | Writes adjudication result back |
| `AppApiPollMethod` | `POST` | GET/POST/etc. for polling endpoint |
| `AppApiRecordsField` | `records` | Optional JSON collection wrapper |
| `AppApiScope` | `api://.../.default` | Entra audience/scope for Managed Identity |
| `ApimBaseUrl` | `https://claims-api.contoso.local` | APIM FQDN |
| `ApimHealthPath` | `/health` | APIM/backend health operation |
| `ApimAdjudicationPath` | `/claims/adjudicate` | Claim submission operation |
| `ApimScope` | `api://.../.default` | Entra audience/scope for APIM |
| `ApimRequestTimeoutSeconds` | `15` | Max same-connection adjudication wait |
| `UniqueIdField` | `UniqueId` | JSON property used as unique claim ID |
| `MaxConcurrentRequests` | `50` | Maximum concurrent 1:1 adjudications |
| `RetryHttpStatusCodes` | `404,408,429,500,502,503,504` | Transport/HTTP retry classification |
| `JsonResponseCodeField` | `ResponseCode` | Field inspected in successful JSON responses |
| `JsonRetryCodes` | `420,421` | JSON codes that enqueue the ID |
| `JsonAlertCodes` | `419` | JSON codes logged as business alerts |
| `ServiceBusFullyQualifiedNamespace` | `name.servicebus.windows.net` | Service Bus namespace FQDN |
| `ServiceBusQueueName` | `pulse-claim-retry` | Retry queue |
| `ServiceBusMessageTtlMinutes` | `10` | Per-message TTL |
| `CircuitFailureThreshold` | `5` | Consecutive outbound failures before opening circuit |
| `CircuitOpenSeconds` | `30` | Local circuit-open duration |

## Security configuration

Recommended Azure configuration:

- Function App: Flex Consumption, Linux, .NET 10 isolated.
- Enable system-assigned Managed Identity.
- VNet-integrate the Function App outbound path.
- Internal App API should be private/restricted and reachable only through approved networking.
- APIM should use private connectivity where architecture permits.
- Service Bus should have public network access disabled and a Private Endpoint.
- Grant Function Managed Identity `Azure Service Bus Data Sender` on the retry queue/namespace.
- Protect App API and APIM with Microsoft Entra ID and grant the Function Managed Identity only the required application role/scope.
- Do not store SQL credentials in this Function. SQL remains behind the internal App API.
- Do not log claim payloads. The code logs the configured UniqueID and operational metadata only.
- Use an `Idempotency-Key`/correlation header based on UniqueID on the APIM request. The downstream system should honor idempotency if it supports it.

## Health contract

The internal App API `/health` endpoint should return non-2xx, preferably HTTP 503, when the API cannot safely execute the claim stored procedure. It should validate the underlying SQL dependency rather than only reporting that the web process is running.

The APIM `/health` operation should represent whether the path to the adjudication backend is usable. It must be lightweight and must not submit a real claim.

When either health probe fails, no new claims are requested from the App API. The timer continues running so the service automatically resumes after health recovers.

## Retry behavior

The retry queue contains only:

```json
{
  "UniqueId": "A100"
}
```

The message also includes `reason` as a Service Bus application property for operations telemetry. Claim JSON is not placed on Service Bus.

This project intentionally does not automatically resubmit a claim if the third-party adjudication succeeded but the result writeback to the internal App API failed. Automatically repeating the adjudication could create a duplicate business transaction. The code retries only the writeback call and then emits `ClaimResultWritebackFailed` telemetry for alerting.

`ProjectPulse.Retry` should later consume `pulse-claim-retry`, wait for dependencies to be healthy, retrieve the current claim by UniqueID from the internal App API, and resubmit through APIM.

## Service Bus queue configuration

Set queue `DefaultMessageTimeToLive` to 10 minutes as a server-side guardrail even though each message also receives a 10-minute TTL in code.

Recommended queue settings:

- Default TTL: 10 minutes
- Dead-lettering on message expiration: Enabled
- Duplicate detection: Consider enabling with a window appropriate to the POC, because `MessageId` is the UniqueID

This means expired retry records stop normal processing after 10 minutes but remain visible in the DLQ for alerting/audit rather than disappearing silently.

## Azure Monitor / Application Insights signals

The code emits events/metrics suitable for Azure Monitor alerts:

- `DependencyHealth.AppApi`
- `DependencyHealth.ApimAndBackend`
- `DependencyHealth` event
- `ClaimPollSkippedDependencyUnhealthy`
- `ClaimsReturnedFromAppApi`
- `ClaimQueuedForRetry`
- `ClaimBusinessAlert`
- `ClaimNonRetryHttpFailure`
- `ClaimResultWritebackFailed`
- `ClaimPollCompleted`

Recommended alerts:

1. App API health metric equals 0 for 3 consecutive polls.
2. APIM/backend health metric equals 0 for 3 consecutive polls.
3. `ClaimResultWritebackFailed` > 0.
4. Retry queue active message count above expected threshold.
5. Retry queue DLQ message count > 0.
6. P95/P99 adjudication duration over the agreed SLA.
7. Retry percentage above an agreed threshold.

## Current assumptions

- App API owns SQL/SP access and duplicate prevention.
- Each returned claim is a unique record.
- Third-party response is returned on the same HTTP connection.
- One claim equals one APIM request and one response.
- The exact API paths and JSON schema are placeholders and are intentionally configuration driven.

## Landing page

Project Pulse includes a branded HTTP landing page at the Function App root URL:

```text
https://<function-app-name>.azurewebsites.net/
```

The page uses the Project Pulse logo embedded in the application assembly, so it does not depend on external image hosting. The logo is exposed internally by the app at `/pulse-logo`.

The bottom-left version label is controlled entirely by application settings:

| Setting | Example | Purpose |
| --- | --- | --- |
| `PROJECT_PULSE_VERSION` | `0.1.0` | Version displayed on the landing page |
| `PROJECT_PULSE_ENVIRONMENT` | `POC` | Environment displayed beside the version |

The values are read at request time, so changing them in the Function App environment settings updates the landing page without changing the source code. The landing page is intentionally informational only and does not expose dependency health, claim data, queue state, or other operational details.

Because `host.json` sets the HTTP `routePrefix` to an empty string, HTTP-triggered Functions are exposed without the default `/api` prefix. The timer-triggered claim processor is unaffected.

## Configuration examples

The repository includes two safe configuration templates that can be committed to GitHub:

- `environment.example.json` is the human-readable reference grouped by feature area. It documents every environment variable currently used by Project Pulse without containing real credentials or production endpoints.
- `local.settings.example.json` uses the Azure Functions local settings format. Developers can copy this file to `local.settings.json` for local development.

Create a local settings file with:

```powershell
Copy-Item local.settings.example.json local.settings.json
```

`local.settings.json` is intentionally ignored by Git and must not be committed. Azure Function App settings are configured in Azure as application settings. The example files are documentation/templates only and do not automatically configure the deployed Function App.

### Environment variable reference

| Setting | Required | Default / Example | Purpose |
| --- | --- | --- | --- |
| `PROJECT_PULSE_VERSION` | No | `0.1.0` | Version shown on the Project Pulse landing page |
| `PROJECT_PULSE_ENVIRONMENT` | No | `POC` | Environment label shown on the landing page |
| `AzureWebJobsStorage` | Yes for Functions runtime | `UseDevelopmentStorage=true` locally | Azure Functions runtime storage |
| `FUNCTIONS_WORKER_RUNTIME` | Yes | `dotnet-isolated` | .NET isolated worker selection |
| `POLL_SCHEDULE` | Yes | `0 * * * * *` | NCRONTAB schedule for the polling Function |
| `MaxConcurrentRequests` | No | `50` | Maximum number of independent claims processed concurrently per poll invocation |
| `AppApiBaseUrl` | **Yes** | `https://internal-api.example.com` | Base URL of the internal application API |
| `AppApiHealthPath` | No | `/health` | Internal API dependency-health endpoint |
| `AppApiPendingClaimsPath` | No | `/claims/pending` | Endpoint used to retrieve pending claims |
| `AppApiResultPathTemplate` | No | `/claims/{uniqueId}/result` | Endpoint used to write adjudication results back |
| `AppApiPollMethod` | No | `POST` | HTTP method used for the polling request |
| `AppApiRecordsField` | No | `records` | Optional property containing returned claim records |
| `AppApiTimeoutSeconds` | No | `10` | Timeout for internal App API calls |
| `AppApiScope` | Depends on auth | `api://.../.default` | Entra scope requested by the Function managed identity |
| `ApimBaseUrl` | **Yes** | `https://claims-api.example.com` | APIM base URL |
| `ApimHealthPath` | No | `/health` | APIM / downstream health operation |
| `ApimAdjudicationPath` | No | `/claims/adjudicate` | APIM operation used for claim adjudication |
| `ApimRequestTimeoutSeconds` | No | `15` | Maximum time to wait for the same-connection adjudication response |
| `ApimScope` | Depends on auth | `api://.../.default` | Entra scope requested for APIM |
| `UniqueIdField` | No | `UniqueId` | JSON field containing the claim identifier |
| `RetryHttpStatusCodes` | No | `404,408,429,500,502,503,504` | HTTP responses that place the UniqueID on the retry queue |
| `JsonResponseCodeField` | No | `ResponseCode` | JSON property inspected for business response codes |
| `JsonRetryCodes` | No | empty | Business response codes that should queue the UniqueID for retry |
| `JsonAlertCodes` | No | `419` | Business response codes that should emit alert telemetry |
| `ServiceBusFullyQualifiedNamespace` | **Yes** | `sb-pulse-dev.servicebus.windows.net` | Service Bus namespace used by the retry publisher |
| `ServiceBusQueueName` | No | `pulse-claim-retry` | Retry queue name |
| `ServiceBusMessageTtlMinutes` | No | `10` | Time-to-live assigned to retry messages |
| `CircuitFailureThreshold` | No | `5` | Consecutive outbound failures required to open the local circuit breaker |
| `CircuitOpenSeconds` | No | `30` | Time the circuit remains open before allowing another attempt |
| `ResultWriteMaxAttempts` | No | `3` | Maximum attempts to write a completed adjudication result back to the internal API |
| `ResultWriteRetryDelayMilliseconds` | No | `500` | Delay between result-write retry attempts |

Values such as client secrets, passwords, SQL connection strings, or third-party credentials should not be added to either example file. Project Pulse is designed to use Managed Identity for Azure dependencies. Any unavoidable secret should be stored in Azure Key Vault and referenced from Function App configuration rather than committed to the repository.
