# Project Pulse

Project Pulse is a .NET 10 isolated Azure Functions application that polls an internal request API, transforms queued claim requests, sends them to APIM for adjudication, and buffers work to Azure Service Bus when APIM is unavailable.

This README documents the behavior of the current `main` build.

## Current architecture

```text
RTA / App API
    |
    |  health + queued request polling
    v
Project Pulse
    |
    |-- APIM healthy -----------------------> APIM / adjudication backend
    |
    |-- APIM temporarily unavailable ------> Azure Service Bus
    |
    '-- RTA unhealthy / outage threshold ---> stop polling
```

Project Pulse is currently a timer-triggered Azure Function. One timer invocation performs one health-check / poll / process cycle and then exits.

The current POC schedule is:

```text
POLL_SCHEDULE=0 * * * * *
```

which runs once per minute.

## Runtime and project

- Runtime: Azure Functions isolated worker
- Framework: .NET 10
- Primary project: `ProjectPulse.Processor.csproj`
- Primary timer Function: `PollClaims`
- Health Function: `PulseHealth`
- Landing page Function: `PulseLandingPage`
- Retry transport: Azure Service Bus
- Authentication: Managed Identity / `DefaultAzureCredential`
- Telemetry: Application Insights

## Polling behavior

Each `PollClaims` invocation performs the following sequence:

1. Check RTA API health.
2. Check RTA DB/App health.
3. Check APIM/backend health.
4. If RTA is unhealthy, stop immediately and do not poll for claims.
5. Evaluate the current APIM outage state.
6. Depending on the configured outage mode:
   - send normally to APIM,
   - continue polling temporarily and buffer to Service Bus, or
   - stop polling.
7. Poll the RTA/App API for queued requests.
8. Transform the returned records into the APIM contract.
9. Submit the transformed records as one APIM batch, or buffer each record to Service Bus.
10. Emit telemetry and finish the invocation.

The Function does not stay open between timer executions.

## RTA mode

When:

```text
APP_API_MODE=RTA
```

Project Pulse calls:

```text
GET {RtaQueuedPath}?page=1&limit={RtaPageSize}
```

and continues paging up to `RtaPagesPerPoll`.

Current limits enforced by code:

- `RtaPageSize`: 1-100
- `RtaPagesPerPoll`: 1-3

The RTA response is expected to contain:

```json
{
  "Items": [
    {
      "REQUEST_ID": 3348,
      "CORRELATION_ID": "7d3f6d8a-3d04-4f4c-b1ca-5880c79e8d6d",
      "IDEMPOTENCY_KEY": "50c7f236-67bd-4a9e-8d2d-2fb65d7d1d3d",
      "REQUEST_TYPE": "INITIAL_REQUEST",
      "METADATA": {}
    }
  ],
  "HasNextPage": true
}
```

Polling stops early when `HasNextPage=false`.

## Mock API mode

When:

```text
APP_API_MODE=MOCK
```

Project Pulse calls:

```text
GET {RtaQueuedPath}?maxRecords={RtaPageSize}
```

The mock response can be either a raw JSON array or an object containing an `Items` array.

Current POC values:

```text
APP_API_MODE=MOCK
RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaDbHealthPath=/health
CorrelationIdField=uniqueId
```

Mock correlation IDs can also be resolved from either `uniqueId` or `UniqueId`.

## Health behavior

### RTA health

Both configured RTA health endpoints are checked:

```text
RtaHealthPath
RtaDbHealthPath
```

A healthy RTA response must:

- return an HTTP success status, and
- contain:

```json
{
  "IS_HEALTHY": true
}
```

If either RTA health check fails, Project Pulse does not request new claims.

### APIM health

Project Pulse sends a GET request to:

```text
{ApimBaseUrl}{ApimHealthPath}
```

Any successful HTTP status is considered healthy.

## APIM outage handling

The supported outage modes are:

```text
BUFFER_THEN_STOP
STOP_IMMEDIATELY
BUFFER_CONTINUOUSLY
```

The recommended mode is:

```text
APIM_OUTAGE_MODE=BUFFER_THEN_STOP
APIM_OUTAGE_POLL_STOP_SECONDS=300
```

With this mode:

```text
APIM healthy
  -> poll RTA
  -> transform
  -> send batch to APIM

APIM unavailable for less than 300 seconds
  -> continue polling RTA
  -> transform
  -> buffer each record to Service Bus

APIM unavailable for 300 seconds or longer
  -> stop polling RTA

APIM recovers
  -> clear outage state
  -> resume normal polling

APIM unavailable + Service Bus send fails
  -> stop polling immediately
```

The APIM outage timer is currently stored in memory. A Function host recycle, restart, or new process instance resets that timer. This is acceptable for the current POC but should be externalized if the outage timer must survive process restarts in production.

## APIM payload transformation

Field mapping is configuration driven through `ApimFieldMapping`.

Example:

```json
{
  "correlationId": "CORRELATION_ID",
  "requestId": "REQUEST_ID",
  "idempotencyKey": "IDEMPOTENCY_KEY",
  "requestType": "REQUEST_TYPE",
  "patientIcn": "METADATA.PATIENT_ICN",
  "sponsorIcn": "METADATA.SPONSOR_ICN",
  "startDateOfService": "METADATA.START_DATE_OF_SERVICE",
  "endDateOfService": "METADATA.END_DATE_OF_SERVICE",
  "program": "METADATA.PROGRAM",
  "includeAdjustmentDetails": "METADATA.INCLUDE_ADJUSTMENT_DETAILS"
}
```

Dot-delimited source paths are supported.

With:

```text
ApimBatchRootProperty=claims
```

the outbound payload is:

```json
{
  "claims": [
    {
      "correlationId": "...",
      "patientIcn": "...",
      "program": "..."
    }
  ]
}
```

If `ApimBatchRootProperty` is empty, Project Pulse sends a raw JSON array.

## APIM request behavior

The transformed records from a poll are sent in one POST request to:

```text
{ApimBaseUrl}{ApimAdjudicationPath}
```

The request includes:

```text
X-Correlation-ID: <generated batch ID>
Idempotency-Key: <generated batch ID>
X-Pulse-Record-Count: <number of records>
```

The current idempotency header is batch-level, not per-claim.

The APIM request is cancelled after:

```text
ApimRequestTimeoutSeconds
```

## Retry and failure classification

### Retryable HTTP responses

The default retryable status codes are:

```text
404,408,429,500,502,503,504
```

When APIM returns one of those statuses, each claim in the submitted batch is buffered individually to Service Bus.

### Network failures and timeouts

APIM network failures and request timeouts also buffer each claim individually to Service Bus.

### Non-retry HTTP responses

A non-success status that is not listed in `RetryHttpStatusCodes` is classified as `Alerted` and is not automatically queued.

### JSON business codes

For successful HTTP responses, Project Pulse can inspect a configured response property:

```text
JsonResponseCodeField=ResponseCode
```

Codes in `JsonRetryCodes` are buffered to Service Bus.

Codes in `JsonAlertCodes` are reported as business alerts.

Current POC alert configuration:

```text
JsonAlertCodes=419
```

## Circuit breaker

Project Pulse contains an in-process outbound circuit breaker.

Defaults:

```text
CircuitFailureThreshold=5
CircuitOpenSeconds=30
```

After the configured number of consecutive APIM failures, the circuit opens. While open, records are sent to the Service Bus retry path instead of attempting APIM.

After the open period expires, another APIM attempt is allowed.

The circuit breaker state is also process-local and resets on Function host recycle or restart.

## Service Bus buffering

Project Pulse is write-only to Service Bus. It does not consume, replay, or drain buffered messages.

A separate retry/recovery application is expected to consume the queue in the future.

Each buffered claim is written as a `RetryEnvelope`:

```json
{
  "UniqueId": "001",
  "RequestId": null,
  "IdempotencyKey": null,
  "Reason": "ApimUnavailableBufferThenStop",
  "QueuedAtUtc": "2026-10-08T00:46:02Z",
  "OriginalPayload": "{...}",
  "TransformedPayload": "{...}"
}
```

Service Bus message settings:

- `MessageId` = claim unique/correlation ID
- `CorrelationId` = claim unique/correlation ID
- `Subject` = `ClaimRetry`
- TTL = `ServiceBusMessageTtlMinutes`
- application property `reason`
- application property `requestId` when available
- application property `idempotencyKey` when available

Because the current POC queue does not rely on duplicate detection, repeated polling of the same source record can create multiple Service Bus messages with the same `MessageId`.

## Important data-security note

The current Service Bus envelope contains both the original and transformed claim payload.

That is intentional for the current POC, but it means the queue can contain claim data rather than only an identifier.

Production deployment should therefore treat Service Bus as a sensitive data store and apply the same security controls used for other claim-processing components.

## RTA status writeback

Status writeback is controlled by:

```text
RtaStatusWritebackEnabled
```

The current default is:

```text
false
```

When enabled, the current provisional implementation expects APIM to return a JSON array containing correlation IDs. Each response item is sent to:

```text
PUT {RtaStatusPathTemplate}
```

with `{correlationId}` replaced by the returned correlation ID.

Writeback retries are controlled by:

```text
ResultWriteMaxAttempts
ResultWriteRetryDelayMilliseconds
```

This portion of the implementation should be revisited when the final APIM response contract is confirmed.

## Managed Identity and authentication

Project Pulse uses `DefaultAzureCredential`.

For Service Bus, the Function App managed identity should have:

```text
Azure Service Bus Data Sender
```

at the queue or namespace scope.

For the RTA/App API and APIM, bearer tokens are requested only when the corresponding scope is configured:

```text
AppApiScope
ApimScope
```

If a scope is empty, no Authorization header is added.

No Service Bus connection string is required by Project Pulse.

## HTTP endpoints

### Landing page

```text
GET /
GET /pulse
```

The landing page displays the Project Pulse branding plus the configured version and environment.

The logo is served from:

```text
GET /pulse-logo
```

The landing page includes defensive headers such as:

- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- Content Security Policy
- no-cache headers

### Health endpoint

```text
GET /pulse-health
```

The endpoint currently uses anonymous authorization and returns:

```json
{
  "appApi": true,
  "apim": false,
  "serviceBus": true,
  "checkedAt": "..."
}
```

Important: the Service Bus portion of `/pulse-health` currently validates TCP connectivity to port 5671 only. It does not prove that Managed Identity authorization or queue send permissions are working.

The endpoint itself always returns HTTP 200 and communicates dependency state through the JSON booleans.

## Logging and telemetry

Application Insights is enabled for the isolated worker.

Key events and metrics currently emitted include:

- `DependencyHealth`
- `DependencyHealth.<dependency>`
- `ClaimPollSkippedRtaUnhealthy`
- `ApimRecovered`
- `ClaimPollSuspendedForApimOutage`
- `RtaQueuedRecordsReturned`
- `AppApiMockPollFailed`
- `RtaQueuedPollFailed`
- `ClaimBatchBufferedToServiceBus`
- `ClaimQueuedForRetry`
- `ClaimBatchBusinessAlert`
- `RtaStatusWritebackSkipped`
- `RtaStatusWritebackFailed`
- `ClaimPollCompleted`
- `ClaimBatchSize`

### Payload logging

The setting:

```text
LOG_POLL_PAYLOADS=true
```

logs mock/RTA poll payloads and APIM outbound batch payloads.

This is intended only for fake POC data.

For real claim data use:

```text
LOG_POLL_PAYLOADS=false
```

to avoid placing claim payloads in application logs.

## Configuration reference

### Core / runtime

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AzureWebJobsStorage` | Azure Functions runtime | none | Functions host storage |
| `FUNCTIONS_WORKER_RUNTIME` | Runtime | `dotnet-isolated` | Isolated worker |
| `POLL_SCHEDULE` | Yes | none | Timer NCRONTAB expression |
| `PROJECT_PULSE_VERSION` | No | `0.0.0` on page | Landing-page version |
| `PROJECT_PULSE_ENVIRONMENT` | No | `UNKNOWN` on page | Landing-page environment |

### RTA / App API

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AppApiBaseUrl` | Yes | none | Base URL |
| `AppApiScope` | No | empty | Managed Identity token scope |
| `AppApiTimeoutSeconds` | No | `10` | HTTP timeout |
| `APP_API_MODE` | No | `RTA` | `RTA` or `MOCK` |
| `RtaQueuedPath` | No | `/requests/queued` | Queue polling endpoint |
| `RtaHealthPath` | No | `/healthchecks` | API health endpoint |
| `RtaDbHealthPath` | No | `/healthchecks/db/app` | DB/App health endpoint |
| `RtaStatusPathTemplate` | No | `/requests/{correlationId}/status` | Status writeback endpoint |
| `RtaPageSize` | No | `100` | Records per page |
| `RtaPagesPerPoll` | No | `1` | Maximum pages per poll |
| `RtaStatusWritebackEnabled` | No | `false` | Enable status writeback |
| `CorrelationIdField` | No | `CORRELATION_ID` | Correlation field |
| `LOG_POLL_PAYLOADS` | No | `false` | Log request payloads |

### APIM

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `ApimBaseUrl` | Yes | none | APIM base URL |
| `ApimHealthPath` | No | `/health` | Health endpoint |
| `ApimAdjudicationPath` | No | `/claims/adjudicate` | Adjudication operation |
| `ApimRequestTimeoutSeconds` | No | `15` | APIM request timeout |
| `ApimScope` | No | empty | Managed Identity token scope |
| `ApimBatchRootProperty` | No | `claims` | Batch wrapper property |
| `ApimFieldMapping` | No | built-in mapping | RTA-to-APIM field map |
| `APIM_OUTAGE_MODE` | No | `BUFFER_THEN_STOP` | Outage behavior |
| `APIM_OUTAGE_POLL_STOP_SECONDS` | No | `300` | Stop-polling threshold |

### Retry / response handling

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `RetryHttpStatusCodes` | No | `404,408,429,500,502,503,504` | HTTP codes that buffer |
| `JsonResponseCodeField` | No | `ResponseCode` | Response code property |
| `JsonRetryCodes` | No | empty | Business codes that buffer |
| `JsonAlertCodes` | No | `419` | Business codes that alert |
| `ResultWriteMaxAttempts` | No | `3` | Writeback attempts |
| `ResultWriteRetryDelayMilliseconds` | No | `500` | Writeback retry delay |

### Service Bus

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `ServiceBusFullyQualifiedNamespace` | Yes | none | Namespace FQDN |
| `ServiceBusQueueName` | No | `claim-retry` | Retry queue |
| `ServiceBusMessageTtlMinutes` | No | `10` | Message TTL |

### Circuit breaker

| Setting | Required | Default | Purpose |
| --- | --- | --- | --- |
| `CircuitFailureThreshold` | No | `5` | Failures before circuit opens |
| `CircuitOpenSeconds` | No | `30` | Circuit-open duration |

## Current POC application settings

The current mock/API POC should include values equivalent to:

```text
APP_API_MODE=MOCK
LOG_POLL_PAYLOADS=true

RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaDbHealthPath=/health
RtaPageSize=100
RtaPagesPerPoll=1
CorrelationIdField=uniqueId

APIM_OUTAGE_MODE=BUFFER_THEN_STOP
APIM_OUTAGE_POLL_STOP_SECONDS=300

ServiceBusQueueName=projectpulsequeue
ServiceBusMessageTtlMinutes=10
```

For real claim data, `LOG_POLL_PAYLOADS` should be changed to `false`.

## Legacy / currently unused settings

Some older POC settings may still exist in the Azure Function App configuration but are not consumed by the current code.

Examples include:

```text
MAX_CONCURRENT_REQUESTS
CircuitHalfOpenRequests
HealthCheckIntervalSeconds
HealthFailureThreshold
HealthRecoveryThreshold
```

They can be removed after confirming no external automation depends on them.

## Deployment

The GitHub Actions workflow:

```text
.github/workflows/main_projectpulse.yml
```

runs on pushes to `main` and can also be started manually.

Current deployment flow:

1. Checkout the repository.
2. Install .NET 10.
3. Run a Release build into `./output`.
4. Authenticate to Azure using GitHub OIDC.
5. Deploy the build output using `Azure/functions-action`.

No Azure client secret is stored in the workflow. Azure login uses GitHub federated identity credentials plus repository secrets containing the client, tenant, and subscription IDs.

## Security posture

Current design strengths:

- Managed Identity for Azure Service Bus.
- Optional Managed Identity bearer authentication for RTA/App API and APIM.
- No SQL credentials in Project Pulse.
- No Service Bus connection string in application code.
- Configuration kept outside source code.
- GitHub deployment uses OIDC rather than an Azure client secret.
- Landing page includes defensive browser headers.

Recommended production posture:

- VNet integration for the Function App.
- Private Endpoint for Service Bus.
- Private/restricted RTA/App API connectivity.
- Private/restricted APIM connectivity where supported by the target architecture.
- Disable public access where operationally practical.
- Grant only `Azure Service Bus Data Sender` to Project Pulse.
- Keep `LOG_POLL_PAYLOADS=false`.
- Treat Service Bus as sensitive because the current retry envelope contains claim payloads.
- Protect or network-restrict `/pulse-health`.
- Store any unavoidable secrets in Azure Key Vault and reference them from Function App configuration.
- Consider durable APIM outage state if the 5-minute threshold must survive host restarts.
- Consider duplicate detection / end-to-end idempotency before production replay logic is introduced.

## Repository layout

```text
Functions/
  LandingPageFunction.cs
  PollClaimsFunction.cs
  PulseHealthFunction.cs

Models/
  ClaimProcessingResult.cs
  HealthResult.cs
  RetryEnvelope.cs

Options/
  ClaimProcessorOptions.cs

Services/
  ApimOutageTracker.cs
  AppApiClient.cs
  ClaimProcessor.cs
  HealthGate.cs
  ManagedIdentityAuthHeaderProvider.cs
  OutboundCircuitBreaker.cs
  PayloadTransformer.cs
  ServiceBusRetryQueue.cs
  interfaces...

Assets/
  project-pulse-logo.png

Program.cs
host.json
environment.example.json
local.settings.example.json
RTA-BATCH-UPDATE.md
```

## Current production-readiness limitations

The current build is appropriate for the POC, but the following should be addressed before production:

1. APIM outage and circuit-breaker state are in-memory only.
2. Service Bus retry messages currently contain complete payloads.
3. Duplicate queue messages are possible if RTA exposes the same record repeatedly.
4. `/pulse-health` is anonymous.
5. The Service Bus health endpoint checks network connectivity, not sender authorization.
6. RTA status writeback is provisional until the final APIM response contract is confirmed.
7. Automated unit/integration tests have not yet been added to the repository.

## Related project

The POC mock API is maintained separately in:

```text
mialtamo/ProjectPulse-API
```

For the current POC, Project Pulse uses that API as its RTA/App API source while the real RTA integration contract is being finalized.
