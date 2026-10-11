# Project Pulse Current State

## Baseline

This file describes the implementation on `main` at the completion of M0 documentation setup.

## Runtime

- .NET 10 Azure Functions isolated worker.
- Timer-triggered polling.
- Application Insights enabled.
- GitHub Actions deploys `main` to Azure.
- Azure authentication uses GitHub OIDC.
- Runtime Azure authentication uses `DefaultAzureCredential`.

## Implemented Functions

### PollClaims

One timer invocation performs one polling cycle and exits.

Current schedule is controlled by:

`POLL_SCHEDULE`

Current POC value:

`0 * * * * *`

### PulseHealth

Anonymous HTTP endpoint:

`GET /pulse-health`

Returns boolean dependency state for App API, APIM, and Service Bus network reachability.

### PulseLandingPage

Provides:

- `GET /`
- `GET /pulse`
- `GET /pulse-logo`

## Current source polling

Supported modes:

- RTA
- MOCK

RTA polling:

`GET /requests/queued?page=N&limit=N`

Mock polling:

`GET /claims/pending?maxRecords=N`

Current maximum configured design:

- 100 records per page,
- up to 3 pages,
- up to 300 records per poll.

## Current health gate

RTA processing requires both:

- RTA API health,
- RTA DB/App health.

RTA health payload must contain:

`{"IS_HEALTHY": true}`

APIM is healthy when its configured health endpoint returns an HTTP success status.

## Current APIM outage behavior

Recommended configuration:

```text
APIM_OUTAGE_MODE=BUFFER_THEN_STOP
APIM_OUTAGE_POLL_STOP_SECONDS=300
```

Behavior:

- APIM healthy -> normal APIM processing.
- APIM unhealthy under threshold -> poll source and buffer to Service Bus.
- APIM unhealthy at/over threshold -> stop polling source.
- Service Bus send failure during APIM outage -> stop polling.
- APIM recovery -> resume normal flow.

Outage state is currently process-local.

## Current APIM submission

- One APIM POST per poll batch.
- Configurable field mapping.
- Configurable batch root property.
- Configurable timeout.
- Batch-level `X-Correlation-ID`.
- Batch-level `Idempotency-Key`.
- `X-Pulse-Record-Count` header.

Final APIM response contract is not yet established.

## Current retry/failure behavior

Default retry HTTP statuses:

`404,408,429,500,502,503,504`

Network failures and APIM timeouts buffer claims.

Configured JSON retry codes buffer claims.

Configured JSON alert codes are alert-only.

Current alert code:

`419`

## Current Service Bus behavior

Project Pulse only sends messages.

Current envelope includes:

- UniqueId
- RequestId
- IdempotencyKey
- Reason
- QueuedAtUtc
- OriginalPayload
- TransformedPayload

Current POC queue:

`projectpulsequeue`

Current TTL:

10 minutes.

Duplicate messages are currently possible when the mock source exposes the same record repeatedly.

## Current writeback

`RtaStatusWritebackEnabled=false`

Writeback code exists but is provisional because the final APIM response contract is not yet confirmed.

## Current POC mock API

Separate repository:

`mialtamo/ProjectPulse-API`

Relevant endpoint:

`GET /claims/pending`

Known current POC limitation: a static fake record can be exposed repeatedly unless the mock API changes its pending-record behavior.

## Environment variables added for current build

```text
APP_API_MODE
LOG_POLL_PAYLOADS
APIM_OUTAGE_MODE
APIM_OUTAGE_POLL_STOP_SECONDS
```

Current mock values:

```text
APP_API_MODE=MOCK
LOG_POLL_PAYLOADS=true
RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaDbHealthPath=/health
CorrelationIdField=uniqueId
APIM_OUTAGE_MODE=BUFFER_THEN_STOP
APIM_OUTAGE_POLL_STOP_SECONDS=300
```

For real claim data, `LOG_POLL_PAYLOADS` must be false.

## Current Azure/RBAC requirements

Project Pulse Function managed identity requires:

`Azure Service Bus Data Sender`

on the retry queue or namespace.

App API/APIM token access depends on configured Entra scopes.

## Known limitations

- APIM outage state is in memory.
- Circuit breaker state is in memory.
- Service Bus retry messages contain full payloads.
- Duplicate retry messages are possible.
- `/pulse-health` is anonymous.
- Service Bus health only validates TCP connectivity.
- No dedicated automated test project yet.
- Final APIM response contract is not established.
- Final RTA writeback behavior is not enabled.
- Recovery processor does not exist yet.
- High-throughput production architecture has not been load-tested.

## Next milestone

M1 - APIM Mock Adjudication.
