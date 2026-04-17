# BetterStack Uptime Monitoring — Operator Runbook

> Audience: operators configuring external uptime monitoring and on-call alerting for the Contract Lifecycle Engine production deployment at `contracts.kingsleyonoh.com`.
> Scope: monitor endpoints, alert policy, team channels, status-page template, incident runbook excerpt.
> Shipped: Batch 025 (2026-04-17).

## 1. Overview

The Contract Lifecycle Engine exposes three tiers of health probe, each of which BetterStack hits on a fixed cadence. A probe failure pages the on-call channel according to the tier's severity; two consecutive failures on any monitor opens an incident automatically on the public status page.

| Probe | Purpose | Depth |
|-------|---------|-------|
| `GET /health`           | ASP.NET Core is up and responding | process-level |
| `GET /health/db`        | Postgres connection is live (runs `SELECT 1`) | data-plane |
| `GET /health/ready`     | App is functionally ready: DB + (enabled) integrations | readiness |

All three endpoints are public (no API key required) and return JSON. They short-circuit around rate limiting so a misbehaving BetterStack check never locks itself out.

## 2. Creating the Monitors

In the BetterStack UI, create one **HTTP(s) Monitor** per endpoint below:

### Monitor 1 — API Liveness (Priority: P1)

| Field | Value |
|-------|-------|
| Name | `contract-engine :: api live` |
| URL  | `https://contracts.kingsleyonoh.com/health` |
| HTTP method | `GET` |
| Expected status | `200` |
| Expected body contains | `"status":"ok"` |
| Check frequency | `30 s` |
| Regions | at least 2 — recommended `eu-central` + `us-east` |
| Request timeout | `10 s` |
| Alert after | `2 consecutive failures` |

### Monitor 2 — Database Health (Priority: P1)

| Field | Value |
|-------|-------|
| Name | `contract-engine :: db` |
| URL  | `https://contracts.kingsleyonoh.com/health/db` |
| HTTP method | `GET` |
| Expected status | `200` |
| Expected body contains | `"status":"healthy"` |
| Check frequency | `60 s` |
| Regions | at least 2 — recommended `eu-central` + `us-east` |
| Request timeout | `10 s` |
| Alert after | `2 consecutive failures` |

### Monitor 3 — Readiness (Priority: P2)

| Field | Value |
|-------|-------|
| Name | `contract-engine :: ready` |
| URL  | `https://contracts.kingsleyonoh.com/health/ready` |
| HTTP method | `GET` |
| Expected status | `200` or `503` (see note) |
| Expected body contains | `"ready":` |
| Check frequency | `60 s` |
| Regions | `eu-central` |
| Request timeout | `15 s` |
| Alert after | `3 consecutive failures` |

> **Note on `/health/ready`:** the probe returns `200` when every enabled integration passes and `503` when any enabled integration is unhealthy. An integration that is turned OFF via `{SERVICE}_ENABLED=false` is reported as `skipped` and does NOT fail readiness — this is by design, so a partially-onboarded environment doesn't page on services it doesn't use. Don't treat 503 as a full outage on its own; inspect the JSON body to see which integration is degraded.

## 3. Alert Policy

| Tier | Monitors | Alert after | Notify | Acknowledge SLA | Status-page incident |
|------|----------|-------------|--------|-----------------|----------------------|
| P1 — critical | `api live`, `db` | 2 consecutive failures | on-call-primary via Email + SMS + Telegram | 5 min | Auto-posted |
| P2 — degraded | `ready` | 3 consecutive failures | on-call-primary via Email + Telegram | 15 min | Auto-posted |

Acknowledge in BetterStack within the SLA or the alert escalates to on-call-secondary.

### Alert Channels (Team Setup)

Add the following contacts to the BetterStack team:

| Role | Channel |
|------|---------|
| on-call-primary | `harrisononh3@gmail.com`, `+49 ...` (SMS), `@kingsley` (Telegram) |
| on-call-secondary | (TBD — second operator when team expands) |

Escalation order: primary → secondary (after 15 min unacknowledged) → status-page "major outage" label (after 30 min unacknowledged).

## 4. Status Page

Create a **Public Status Page** titled `Contract Lifecycle Engine` and attach all three monitors. Suggested groupings:

- **API** → `api live`
- **Database** → `db`
- **Integrations** → `ready` (this surfaces RAG / Notification Hub / Webhook Engine readiness)

Custom domain: `status.contracts.kingsleyonoh.com` (add `CNAME → statuspage.betterstack.com`).

### Incident Template (posted on auto-incident creation)

```
[Investigating] We are seeing {{monitor_name}} fail health checks. On-call is engaged.

This affects: {{affected_surface}}
Started: {{incident_start}}
Updates every 15 minutes until resolved.
```

Operators can edit the template in BetterStack under **Status Page → Incident Templates**.

## 5. Incident Runbook (excerpt)

When paged:

1. **Acknowledge in BetterStack** so the timer stops escalating.
2. **Check the status page** to see which monitor(s) failed and for how long.
3. **Check Sentry** (`SENTRY_DSN` wired on production) — is there a correlated error spike? Look for events tagged with the `module` property that matches the affected endpoint (e.g. `health`, `webhooks`).
4. **Check Serilog logs** (Docker log aggregation) — filter by `request_id` from the last known-good request ID echoed by the monitor's response body.
5. **If `/health/db` is failing:**
   - Connect to Postgres from the host: `docker compose -f docker-compose.prod.yml exec db pg_isready -U contract_engine`.
   - If DB is down, check disk space on the Hetzner VPS (`df -h`) and `docker compose logs db`.
6. **If `/health/ready` is failing but `/health/db` is OK:**
   - Check the JSON body — it lists each integration's status. A failing ecosystem service (RAG Platform, Notification Hub, etc.) degrades readiness but the core API stays up.
   - Disable the degraded integration via `{SERVICE}_ENABLED=false` in `.env` on the VPS + restart the container. Readiness recovers; the feature falls back to the no-op stub.
7. **If `/health` is failing:**
   - The process is down or wedged. Check `docker compose ps` — is the container restarting?
   - `docker compose logs contract-engine --tail 200` — look for migration failures, missing env vars, or unhandled startup exceptions.
   - Last-resort rollback: `docker compose pull && docker compose up -d` with the previous image tag (tags are immutable on GHCR).

### Post-incident

- Mark the BetterStack incident resolved.
- Post a final status-page update with root cause.
- Capture learnings in `docs/progress.md` as a gotcha — failure modes discovered in production are the most valuable context we have.

## 6. What NOT to Monitor

- **Authenticated endpoints** — they all require an `X-API-Key` header. BetterStack would need to hold a real tenant's key, which would expose the key in the monitor config and require rotation on every operator change. Use `/health/*` instead.
- **`POST /api/webhooks/contract-signed`** — this endpoint is HMAC-gated; synthetic monitoring would require storing a valid signing secret in BetterStack. Covered by readiness + Sentry instead.
- **Background jobs (scanner, extraction, auto-renewal)** — these run in-process and are observable via log events (`deadline_scanner.*`, `extraction_processor.*`). Paged on via Sentry error events, not uptime monitors.

## 7. Rotating BetterStack Tokens

BetterStack API tokens are stored in 1Password under the `contract-engine :: production` vault. Rotate on operator turnover. No code changes needed — BetterStack monitors read URLs only; they do not need any Contract Engine secret.
