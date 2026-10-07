# Project Pulse RTA Batch Update

This update changes the processor from one-claim-at-a-time polling to the current RTA contract.

## RTA polling

- `GET /requests/queued?page=N&limit=100`
- `RtaPageSize` is clamped to 1-100.
- `RtaPagesPerPoll` is clamped to 1-3.
- Polling stops early when `HasNextPage` is false.
- All returned `Items` are combined into one APIM batch.

## Health gate

Both RTA checks must return HTTP success and `{ "IS_HEALTHY": true }`:

- `GET /healthchecks`
- `GET /healthchecks/db/app`

APIM health must also pass before queued requests are pulled.

## APIM transformation

`ApimFieldMapping` is a JSON object where the property name is the APIM target field and the value is a dot-delimited RTA source path.

Example:

```json
{
  "correlationId": "CORRELATION_ID",
  "patientIcn": "METADATA.PATIENT_ICN",
  "program": "METADATA.PROGRAM"
}
```

`ApimBatchRootProperty=claims` produces:

```json
{
  "claims": [ ... ]
}
```

Set it to an empty value later if APIM expects a raw JSON array.

## Status writeback

`RtaStatusWritebackEnabled=false` by default because the final APIM response contract is not known yet.

The configured future endpoint is:

`PUT /requests/{correlationId}/status`

When enabled, the provisional implementation expects APIM to return a JSON array containing one response object per correlation ID.

## Retry behavior

If the whole APIM batch receives a configured retry HTTP status, times out, or has a network failure, Project Pulse puts each `CORRELATION_ID` into Service Bus individually. Full claim payloads are not placed on the queue.
