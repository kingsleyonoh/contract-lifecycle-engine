# Webhook Engine Integration — Operator Runbook

> Audience: operators configuring the upstream Webhook Ingestion Engine to deliver signed-contract events from DocuSign / PandaDoc into the Contract Lifecycle Engine.
> Scope: env-var setup, destination registration, HMAC secret rotation, payload contracts, idempotency, troubleshooting.
> Shipped: Batch 024 (2026-04-17).

## 1. Overview

The Contract Lifecycle Engine exposes a single public webhook endpoint:

```
POST /api/webhooks/contract-signed?source={docusign|pandadoc}
```

The endpoint is **inbound-only**. The Webhook Engine is the authenticated caller — it authenticates via an HMAC-SHA256 signature over the raw request body plus a per-tenant `X-Tenant-Id` header that tells the engine which tenant owns the resulting draft contract.

### Happy-path side effects (in order)
1. HMAC signature verified
2. Tenant resolved from `X-Tenant-Id`
3. Payload parsed and normalised into a `SignedContractPayload`
4. Idempotency probe on the JSONB `metadata` column — if this envelope/document id is already attached to a contract, return the existing contract id
5. New `Draft` contract created with the idempotency key stashed in `metadata`
6. Signed PDF downloaded from the supplied `download_url` and persisted to local document storage
7. Extraction job enqueued (obligations will be parsed by the next `ExtractionProcessorJob` tick)

The handler returns **202 Accepted** once the draft contract exists. Stages 6 and 7 are fire-and-forget — a transient download failure or extraction trigger error never rolls back the draft contract; it stays in `Draft` for human review.

## 2. Required Environment Variables

Set on the API host before starting the server:

| Variable | Required | Purpose |
|----------|----------|---------|
| `WEBHOOK_ENGINE_ENABLED` | yes | Set to `true` to enable the endpoint. Any other value (including absent) returns 404. |
| `WEBHOOK_SIGNING_SECRET` | yes | Shared secret used to verify the HMAC-SHA256 signature. Min 32 chars recommended. Blank secret → 404 (same as disabled). |

When either flag is unset/false, the route responds 404 so port scanners see no hint the endpoint exists. This is intentional — the Webhook Engine is the ONLY legitimate caller and operators should leave the endpoint disabled on any host not receiving real traffic.

### Rotating the signing secret

1. Generate a new secret: `openssl rand -hex 32`
2. Update the Webhook Engine destination(s) with the new secret.
3. Update `WEBHOOK_SIGNING_SECRET` on the API host and restart the API.
4. There is NO overlap window in this first release. If zero-downtime rotation is required, deploy a second API instance with the new secret, cut over the Webhook Engine destinations, then retire the old instance.

## 3. Destination Registration in the Webhook Engine

Each tenant that receives signed contracts needs a Webhook Engine destination. Configure the destination with:

| Field | Value |
|-------|-------|
| URL | `https://contracts.kingsleyonoh.com/api/webhooks/contract-signed?source=docusign` (or `source=pandadoc`) |
| Method | POST |
| Content-Type | `application/json` |
| Signing algorithm | `HMAC-SHA256` |
| Signing secret | the value of `WEBHOOK_SIGNING_SECRET` |
| Signature header | `X-Webhook-Signature` |
| Signature format | `sha256=<hex>` (GitHub convention) — bare `<hex>` also accepted for legacy callers |
| Custom header 1 | `X-Tenant-Id: <tenant_uuid>` — the tenant that owns the resulting contract |
| Retry policy | up to the Webhook Engine — our handler is idempotent so retries are safe |
| Timeout | 30s is more than enough; the synchronous portion finishes in < 500 ms |

### How to get the tenant UUID

Run against the Contract Engine with the tenant's API key:

```sh
curl -H "X-API-Key: cle_live_<32-hex>" https://contracts.kingsleyonoh.com/api/tenants/me
```

The `id` field in the response is the `X-Tenant-Id` to configure on the destination.

## 4. Accepted Payload Shapes

The parser normalises two upstream shapes into `SignedContractPayload`. Anything else returns **202 Accepted** with `{"status":"ignored"}` so the Webhook Engine stops retrying without creating a contract.

### DocuSign (`source=docusign`)

Event: `envelope.completed`. Required JSON fields:

```json
{
  "event": "envelope.completed",
  "envelope_id": "11111111-2222-3333-4444-555555555555",
  "envelope_name": "MSA — Acme Corp 2026",          // optional; falls back to envelope_id
  "completed_at": "2026-04-15T18:22:00Z",           // optional
  "signers": [                                      // optional
    { "company": "Acme Corp" }
  ],
  "documents": [
    {
      "name": "msa-acme-signed.pdf",                // optional; falls back to "<envelope_id>.pdf"
      "download_url": "https://docusign-cdn.example.com/.../signed.pdf"
    }
  ]
}
```

Counterparty resolution: `signers[0].company` → `envelope_name` → `envelope_id`.

### PandaDoc (`source=pandadoc`)

Event: any event containing `state_changed` (covers `document_state_changed` and legacy `document.state_changed`). Required JSON fields inside `data`:

```json
{
  "event": "document_state_changed",
  "data": {
    "id": "hhmGyNQwUoHZWuJrbBDP4m",
    "status": "document.completed",
    "name": "Services Agreement — Acme Corp.pdf",   // optional; falls back to id
    "date_completed": "2026-04-15T18:22:00Z",       // optional
    "download_url": "https://pandadoc-api.example.com/documents/xxx/download",
    "metadata": {
      "counterparty_name": "Acme Corp"              // optional
    }
  }
}
```

Counterparty resolution: `data.metadata.counterparty_name` → `data.name` → `data.id`.

## 5. Response Envelope

All responses from the endpoint use the shapes below. The canonical error envelope (Key Patterns §1) is NOT used for the 401 branch because the handler returns `IResult` directly rather than throwing — the shape is still kept consistent with the rest of the API.

| Status | Body | Meaning |
|--------|------|---------|
| 202 Accepted | `{"status":"accepted","contract_id":"<guid>","idempotent":false}` | New draft contract created. |
| 202 Accepted | `{"status":"accepted","contract_id":"<guid>","idempotent":true}` | Redelivery — returning existing contract id. |
| 202 Accepted | `{"status":"ignored"}` | Parsed payload was non-actionable (wrong event type, malformed JSON, unsupported source). Acked so the Webhook Engine stops retrying. |
| 401 Unauthorized | `{"error":{"code":"UNAUTHORIZED","message":"Webhook signature or tenant verification failed"}}` | HMAC mismatch, missing/unknown/inactive tenant, or malformed `X-Tenant-Id`. |
| 404 Not Found | (empty) | Endpoint disabled via `WEBHOOK_ENGINE_ENABLED=false` or blank `WEBHOOK_SIGNING_SECRET`. |
| 429 Too Many Requests | canonical `RATE_LIMITED` envelope | Public webhook bucket exhausted (100/min per client IP — see `RATE_LIMIT__PUBLIC_WEBHOOK`). |

## 6. Idempotency Guarantees

Redeliveries of the same envelope (DocuSign) or document (PandaDoc) are idempotent:

- Draft contract `metadata` is stamped with either `webhook_envelope_id` (DocuSign) or `webhook_document_id` (PandaDoc).
- On every webhook arrival the handler probes the JSONB `metadata` column via `EF.Functions.JsonContains` for the same external id scoped to the tenant.
- A hit short-circuits to the existing `contract_id` with `idempotent=true`.
- A miss creates a new draft contract.

This means the Webhook Engine's retry policy is safe — even if it redelivers a message 10 times, the engine produces exactly one contract row. The `webhook_received_at` / `signed_completed_at` timestamps persist on the ORIGINAL contract; we do not update them on redelivery.

## 7. Rate Limiting

The endpoint is behind the `PublicWebhook` policy (default **100/min** partitioned by client IP) rather than the stricter `Public` policy (5/min) used for tenant registration. Rationale: the Webhook Engine batches signed-contract deliveries on completion bursts for large deals (e.g. an MSA + 3 SOWs signed at the same moment). The tighter 5/min would drop legitimate envelopes.

Override via env var: `RATE_LIMIT__PUBLIC_WEBHOOK=200` (higher) or `=50` (stricter).

HMAC signature verification gates abuse, not rate limiting.

## 8. Troubleshooting

### 401 on every delivery
- **Signature mismatch.** Verify the signing secret in the Webhook Engine matches `WEBHOOK_SIGNING_SECRET` exactly. No leading/trailing whitespace, no newline.
- **Wrong body hashing.** The HMAC must be over the raw request body, not the JSON-stringified body. If the Webhook Engine re-serialises, the hash will not match. We use `Encoding.UTF8.GetBytes(body)` server-side.
- **Wrong header format.** Accepted: `X-Webhook-Signature: sha256=<hex>` (preferred) or bare `<hex>` (legacy).
- **Missing / invalid `X-Tenant-Id`.** The header must be a valid Guid that maps to an ACTIVE tenant row. Inactive tenants → 401.

### 404 on every delivery
- `WEBHOOK_ENGINE_ENABLED` is not set to `true` (case-sensitive).
- `WEBHOOK_SIGNING_SECRET` is blank. The endpoint will also 404 in this state — this is intentional.

### 202 with `status=ignored` but no contract
- Payload is non-actionable. For DocuSign: `event` must be exactly `envelope.completed`. For PandaDoc: `data.status` must be `document.completed`.
- Malformed JSON parses to `null` → 202 ignored. Check the Webhook Engine delivery log for the body that was sent.

### 202 ack but contract missing document / extraction
- Download stage is fire-and-forget. Check the API log for `Webhook document download/upload failed for contract <id>` warnings.
- Common causes: signed URL expired, upstream auth required, network egress blocked. Re-trigger manually after investigating.

### Draft contracts pile up with no extraction
- Check `ExtractionProcessorJob` is running (`JOBS_ENABLED=true`, Quartz logs show `Triggering ExtractionProcessorJob`).
- Check `RAG_PLATFORM_ENABLED=true` AND the credentials are valid. Without RAG, extraction jobs stay in `Queued`.

### Log signals to monitor
- `Webhook rejected: signature verification failed` — repeated occurrences indicate secret drift or an impostor caller.
- `Webhook rejected: X-Tenant-Id header missing or malformed` — destination misconfigured or tenant id typoed.
- `Webhook rejected: tenant <guid> not found or inactive` — tenant deactivated since the destination was registered.
- `Webhook idempotency hit — returning existing contract <id>` — expected on retries; high volume on a specific `external_id` may indicate the Webhook Engine's retry policy is too aggressive.
- `Webhook parsed to null (non-actionable); acking with status=ignored` — normal for events we don't care about (e.g. `envelope.sent`).

## 9. Security Notes

- The signing secret must never be committed. `.env.example` contains a placeholder only.
- Raw request bodies are buffered in memory for the duration of the request; they are not persisted or logged.
- `X-Webhook-Signature` is compared using `CryptographicOperations.FixedTimeEquals` on byte arrays to prevent timing attacks.
- The download URL is NEVER logged (may contain signed credentials). We log `contract_id` and `payload.Source` only.
- `WEBHOOK_ENGINE_ENABLED=false` returns 404 (not 403) so port scanners cannot confirm the endpoint exists.

## 10. Testing the Endpoint End-to-End

Use curl to simulate a DocuSign delivery:

```sh
BODY='{"event":"envelope.completed","envelope_id":"11111111-2222-3333-4444-555555555555","envelope_name":"Test MSA","completed_at":"2026-04-15T18:22:00Z","signers":[{"company":"Acme Corp"}],"documents":[{"name":"signed.pdf","download_url":"https://httpbin.org/bytes/1024"}]}'
SECRET='<same as WEBHOOK_SIGNING_SECRET>'
SIG=$(printf "%s" "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -hex | awk '{print $2}')
TENANT_ID='<tenant_uuid>'

curl -X POST "https://contracts.kingsleyonoh.com/api/webhooks/contract-signed?source=docusign" \
  -H "Content-Type: application/json" \
  -H "X-Webhook-Signature: sha256=$SIG" \
  -H "X-Tenant-Id: $TENANT_ID" \
  --data "$BODY"
```

Expected: 202 with `status=accepted`. A second invocation with the same `envelope_id` returns `idempotent=true`.
