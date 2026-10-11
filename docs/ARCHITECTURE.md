# Project Pulse Architecture

## Current topology

```text
RTA / App API
   |
   | health + queued requests
   v
Project Pulse Azure Function
   |
   +--> APIM --> adjudication backend
   |
   +--> Azure Service Bus retry queue
```

## Runtime

- Azure Functions
- .NET 10 isolated worker
- Timer-triggered polling
- Application Insights telemetry
- Managed Identity through `DefaultAzureCredential`

## Main components

### PollClaimsFunction

Coordinates one polling cycle.

Responsibilities:

- dependency health checks,
- APIM outage decision,
- source polling,
- dispatch to APIM or Service Bus,
- run-level telemetry.

### HealthGate

Checks:

- RTA API health,
- RTA DB/App health,
- APIM/backend health.

RTA health requires HTTP success plus:

```json
{"IS_HEALTHY": true}
```

### AppApiClient

Supports:

- RTA paged polling,
- mock polling,
- optional result/status writeback.

Current paging limits:

- page size 1-100,
- pages per poll 1-3.

### PayloadTransformer

Maps source JSON paths to APIM target fields using `ApimFieldMapping`.

### ClaimProcessor

Current orchestration service for:

- APIM batch construction,
- APIM POST,
- timeout handling,
- HTTP/business-code classification,
- circuit breaker interaction,
- Service Bus fallback,
- optional status writeback.

This class intentionally remains consolidated during POC development. It can be decomposed after contracts stabilize.

### ApimOutageTracker

Process-local outage state.

Supported actions:

- SendToApim
- BufferToServiceBus
- StopPolling

Supported modes:

- BUFFER_THEN_STOP
- STOP_IMMEDIATELY
- BUFFER_CONTINUOUSLY

Current recommended threshold:

`APIM_OUTAGE_POLL_STOP_SECONDS=300`

Important limitation: outage state is in memory and resets on host recycle/restart.

### OutboundCircuitBreaker

Process-local APIM failure circuit.

Default behavior:

- open after 5 consecutive failures,
- remain open for 30 seconds,
- then allow another APIM attempt.

### ServiceBusRetryQueue

Writes one retry envelope per claim to Azure Service Bus.

Project Pulse does not consume this queue.

## Current timer model

The Function is not a long-running polling worker.

With:

`POLL_SCHEDULE=0 * * * * *`

behavior is:

```text
00:00 invoke -> check -> poll/process -> exit
01:00 invoke -> check -> poll/process -> exit
02:00 invoke -> check -> poll/process -> exit
```

## Current APIM batch model

All claims returned in one poll are transformed and sent in one APIM POST.

With:

`ApimBatchRootProperty=claims`

the payload shape is:

```json
{
  "claims": [
    { "correlationId": "...", "patientIcn": "..." }
  ]
}
```

## Current failure model

```text
RTA unhealthy
-> stop polling

APIM healthy
-> poll RTA
-> transform
-> APIM

APIM unavailable for short outage
-> poll RTA
-> transform
-> Service Bus

APIM outage threshold reached
-> stop polling RTA

APIM unavailable + Service Bus send fails
-> stop polling immediately

APIM recovers
-> resume normal processing
```

## Future architecture

Later milestones add:

- APIM mock adjudication,
- complete status writeback,
- duplicate/idempotency controls,
- separate Service Bus recovery processor,
- durable outage state,
- stronger observability,
- performance/scale validation.
