# Notification Hub Template Onboarding — Operator Runbook

> Audience: operators provisioning the Event-Driven Notification Hub so Contract Engine events (obligation deadlines, contract expiries, auto-renewals, conflicts, extraction completion) produce the right email / Telegram bodies.
> Scope: env-var setup, one-shot CLI invocation, expected output, failure recovery, template catalogue.
> Shipped: Batch 025 (2026-04-17).

## 1. Overview

Contract Engine publishes 8 canonical event types to the Notification Hub (PRD §5.6b). Each event type has a corresponding **template** registered on the Hub that renders the subject line and markdown body when the event fires.

The template seeder is a one-shot CLI that POSTs the 8 templates to Hub's `POST /api/templates` endpoint. Running it is a manual, operator-driven step — it is NOT tied to `AUTO_SEED` because Hub may be unreachable during a fresh first boot, and we don't want the API host to thrash retries on every container restart.

### The 8 Canonical Templates

| Type | Trigger | Subject placeholder set |
|------|---------|-------------------------|
| `deadline_approaching` | Obligation enters the alert window (`90/30/14/7/1` days out) | `obligation_title`, `contract_title`, `days_remaining`, `deadline_date`, `responsible_party`, `obligation_url` |
| `deadline_imminent`    | Obligation within the last 1–2 business days | same as `deadline_approaching` |
| `overdue`              | Obligation transitioned past its deadline | `obligation_title`, `contract_title`, `days_overdue`, `deadline_date`, `responsible_party`, `obligation_url` |
| `escalated`            | Obligation overdue > `OVERDUE_ESCALATION_DAYS` business days (default 14) | same as `overdue` |
| `contract_expiring`    | Contract nearing `end_date` (default 90 days) | `contract_title`, `counterparty_name`, `days_remaining`, `end_date`, `contract_url` |
| `auto_renewed`         | Contract auto-renewed by the `AutoRenewalMonitorJob` | `contract_title`, `counterparty_name`, `renewal_period_months`, `end_date`, `contract_url` |
| `contract_conflict`    | `ConflictDetectionService` flagged a cross-contract conflict on activation | `contract_title`, `conflicting_contract_title`, `counterparty_name`, `conflict_summary`, `contract_url` |
| `extraction_complete`  | `ExtractionProcessorJob` finished a job with ≥ 1 pending obligation | `contract_title`, `obligations_found`, `obligations_pending`, `extraction_url` |

Each template's body is defined in `NotificationHubTemplateSeeder.Templates` (`src/ContractEngine.Infrastructure/Data/NotificationHubTemplateSeeder.cs`). Editing a body requires re-running the seeder — Hub will return `409 Conflict` on the duplicate, so bodies are append-only in practice unless you delete the template on Hub first.

## 2. Required Environment Variables

Set on the API host (or your workstation if invoking against a staging Hub) before running the seeder:

| Variable | Required | Purpose |
|----------|----------|---------|
| `NOTIFICATION_HUB_URL` | yes | Base URL of the Hub. No trailing slash. Example: `https://hub.kingsleyonoh.com` |
| `NOTIFICATION_HUB_API_KEY` | recommended | Hub system API key. Sent as `X-API-Key` header on each template POST. Optional in dev (Hub can be unauthenticated locally). |

If `NOTIFICATION_HUB_URL` is empty the seeder fails fast with exit code `1` and a clear log message — nothing hits the network.

## 3. Invoking the Seeder

From the repo root:

```bash
dotnet run --project src/ContractEngine.Api -- --seed-hub-templates
```

Or inside the production container:

```bash
docker compose -f docker-compose.prod.yml run --rm contract-engine dotnet /app/ContractEngine.Api.dll --seed-hub-templates
```

The CLI short-circuits before the web host starts — no Kestrel bind, no background scheduler, no DB migration. The process exits with code `0` on full success and `1` on any non-409 failure.

### Expected Output (Happy Path)

```
[INF] Seeded template 'deadline_approaching' — Hub responded 201
[INF] Seeded template 'deadline_imminent' — Hub responded 201
[INF] Seeded template 'overdue' — Hub responded 201
[INF] Seeded template 'escalated' — Hub responded 201
[INF] Seeded template 'contract_expiring' — Hub responded 201
[INF] Seeded template 'auto_renewed' — Hub responded 201
[INF] Seeded template 'contract_conflict' — Hub responded 201
[INF] Seeded template 'extraction_complete' — Hub responded 201
[INF] NotificationHubTemplateSeeder: all 8 templates registered successfully
```

### Expected Output (Re-run — Idempotent)

Running a second time on an already-seeded Hub:

```
[INF] Template 'deadline_approaching' already exists on Hub (409 Conflict) — treating as success
[INF] Template 'deadline_imminent' already exists on Hub (409 Conflict) — treating as success
...
[INF] NotificationHubTemplateSeeder: all 8 templates registered successfully
```

Exit code `0`. The seeder is safe to re-run whenever an operator wants to verify Hub state.

### Expected Output (Partial Failure)

If Hub is degraded and returns `500` on, say, 3 templates while accepting the other 5:

```
[INF] Seeded template 'deadline_approaching' — Hub responded 201
[INF] Seeded template 'deadline_imminent' — Hub responded 201
[ERR] Template 'overdue' failed with 500: <body>
[INF] Seeded template 'escalated' — Hub responded 201
[ERR] Template 'contract_expiring' failed with 500: <body>
[INF] Seeded template 'auto_renewed' — Hub responded 201
[INF] Seeded template 'contract_conflict' — Hub responded 201
[ERR] Template 'extraction_complete' failed with 500: <body>
[ERR] NotificationHubTemplateSeeder: 3 of 8 templates failed — re-run after fixing Hub issues
```

Exit code `1`. The seeder continues through every template on failure (rather than aborting on the first one) so Hub operators see the full picture. Once Hub is healthy, re-run and the 5 already-seeded templates return 409 (benign) while the 3 missing ones create.

## 4. Verifying Hub Registration

Hub exposes `GET /api/templates` (no template-creation auth needed for reads). Confirm the 8 templates exist:

```bash
curl -s https://hub.kingsleyonoh.com/api/templates | jq '[.data[].type] | sort'
```

Expected output:

```json
[
  "auto_renewed",
  "contract_conflict",
  "contract_expiring",
  "deadline_approaching",
  "deadline_imminent",
  "escalated",
  "extraction_complete",
  "overdue"
]
```

If any type is missing, re-run the seeder.

## 5. Modifying a Template

Template bodies evolve as Contract Engine's events learn new placeholders. The seeder is **create-only** — it cannot update an existing template (409 Conflict is treated as success, not as "update needed").

To change a template's subject or body:

1. Delete the template on Hub: `DELETE /api/templates/{type}` (system API key required — out of scope for this engine).
2. Edit `NotificationHubTemplateSeeder.Templates` in Contract Engine, deploy the new build.
3. Re-run `--seed-hub-templates` on the new build. The deleted template now creates fresh; the other 7 return 409 (idempotent).

## 6. Rollback

If a bad template deploys (e.g. wrong placeholder name → rendering fails on Hub):

1. Delete the affected template on Hub.
2. Revert the Contract Engine commit that changed the template body.
3. Deploy the reverted build.
4. Re-run `--seed-hub-templates`.

The seeder's idempotent-409 behaviour means steps 3–4 will not disturb the 7 templates that were untouched.

## 7. Disabling Notifications Entirely

If Hub is down or an operator needs to silence all notifications (e.g. during a migration window):

```bash
# In .env on the API host:
NOTIFICATION_HUB_ENABLED=false
```

and restart the API container. The `NoOpNotificationPublisher` is wired instead of `NotificationHubClient`, so every event publish becomes a silent no-op. Re-enable by setting back to `true` and restarting. No seeder changes needed — the seeder and runtime are independently flagged.

## 8. Security Notes

- `NOTIFICATION_HUB_API_KEY` is a system-wide key (not a tenant key). Store it in `.env` on the VPS, not in source control. `.env` is git-ignored.
- The seeder never logs the key value — only presence / absence. Review stdout before committing to a ticket or support thread.
- `--seed-hub-templates` does NOT require Sentry, database, or any Contract Engine business state. It can be invoked from a throwaway container with just the two env vars set.
