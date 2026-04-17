# Contract Lifecycle Engine — Codebase Context

> Last updated: 2026-04-17 (Phase 3 close — Batches 024/025, CI/CD + VPS out of scope; post-Batch-025 modularity split pass applied to ContractService / ExtractionService / WebhookEndpoints)
> Template synced: 2026-04-17
> Status: **Phase 2 complete. Phase 3 complete on all in-scope items: Batch 023 (Notification Hub / Workflow Engine / Compliance Ledger NATS / Invoice Recon clients + event wiring), Batch 024 (Webhook Engine inbound — `POST /api/webhooks/contract-signed` with HMAC-SHA256 verification + JSONB idempotency + signed-doc download → extraction chain), Batch 025 (observability — Sentry + Sentry.Serilog wiring with PII scrubbing via `SentryPrivacyFilter`, BetterStack uptime runbook, Notification Hub template seeder + `--seed-hub-templates` CLI). CI/CD pipeline + Hetzner VPS deployment (Batch 026) EXPLICITLY OUT OF SCOPE per 2026-04-17 user direction — Batch 026 was reverted (commit eee50d9). Project remains production-capable via `docker build` + manual deploy; `docker-compose.prod.yml` retains pre-Batch-026 shape.**

## Local Dev Setup (Workstation Bootstrapping)

1. **Clone and `cd`** into the repo.
2. **Copy `.env.example` → `.env`** and leave the defaults. All integration flags default to `false` locally.
3. **Check port 5432 and 4222 availability.** If either is occupied by a sibling PostgreSQL/NATS instance, create a `docker-compose.override.yml` mapping to free host ports — e.g. `5445:5432` and `4225:4222`. Update `DATABASE_URL` and `NATS_URL` in `.env` to match. `docker-compose.override.yml` is git-ignored; each developer maintains their own.
4. **Start services:** `docker compose up -d db` (and `docker compose --profile nats up -d nats` if working on Compliance Ledger).
5. **Verify health:** `docker compose ps` should show `db` as `(healthy)`. Connect check: `docker exec contract-lifecycle-engine-db-1 pg_isready -U contract_engine`.
6. **Run the API:** `dotnet run --project src/ContractEngine.Api`. With `AUTO_MIGRATE=true` (default) and `AUTO_SEED=true` (default), the first boot applies all EF Core migrations, seeds US/DE/UK/NL holiday calendars, and creates a "Default" tenant — the generated API key is printed to stdout once.

The `.env` file is git-ignored — never commit real values.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Language | C# 12 / .NET 8 |
| Framework | ASP.NET Core 8 (Minimal APIs) |
| ORM | Entity Framework Core 8 (Npgsql) |
| Database | PostgreSQL 16 |
| Background Jobs | Quartz.NET 3.x |
| Messaging | NATS.Client 2.x (JetStream) — Phase 3 |
| HTTP Client | IHttpClientFactory (typed clients) — Phase 2/3 |
| Validation | FluentValidation 11.x |
| Logging | Serilog 4.x (structured JSON via `Serilog.AspNetCore` 8.x + `Serilog.Formatting.Compact`) |
| Hosting | Docker on Hetzner VPS — `contracts.kingsleyonoh.com` |
| Package Manager | NuGet / dotnet CLI |
| Test Runner | xUnit 2.x + FluentAssertions + NSubstitute |
| Error Tracking | Sentry (.NET SDK) — Phase 3 |
| Uptime | BetterStack — Phase 3 |

## Project Structure

```
contract-lifecycle-engine/
├── src/
│   ├── ContractEngine.Api/          # ASP.NET Core host — endpoints, middleware, DI
│   │   ├── Endpoints/               # 12 endpoint groups: Tenants, Counterparties, Contracts,
│   │   │                             #   ContractDocuments, ContractTags, ContractVersions,
│   │   │                             #   Obligations, Alerts, Extraction, Analytics, Health, Webhooks
│   │   ├── Endpoints/Dto/           # Request / response DTOs
│   │   ├── Middleware/              # ExceptionHandling, RequestLogging, TenantResolution
│   │   ├── RateLimiting/            # Rate-limit policies + configuration
│   │   └── Program.cs              # Entry point, DI registration, --seed CLI, AUTO_MIGRATE
│   ├── ContractEngine.Core/         # Domain logic — zero external dependencies
│   │   ├── Abstractions/            # ITenantContext, ITenantScoped, NullTenantContext
│   │   ├── Models/                  # 12 EF Core entities (Tenant, Counterparty, Contract,
│   │   │                             #   ContractDocument, ContractTag, ContractVersion,
│   │   │                             #   Obligation, ObligationEvent, DeadlineAlert, HolidayCalendar,
│   │   │                             #   ExtractionPrompt, ExtractionJob)
│   │   ├── Enums/                   # 10 enums (ContractStatus/Type, 5 Obligation enums, AlertType,
│   │   │                             #   DisputeResolution, ExtractionStatus)
│   │   ├── Exceptions/              # EntityTransitionException + Contract/Obligation subclasses
│   │   ├── Interfaces/              # Repository + client abstractions (incl. IWebhookDocumentDownloader)
│   │   ├── Defaults/               # ExtractionDefaults (hardcoded prompt templates)
│   │   ├── Integrations/Rag/       # RAG Platform DTOs (RagDocument, RagSearchResult, RagChatResult, RagEntity, RagPlatformException)
│   │   ├── Integrations/Webhooks/  # SignedContractPayload, WebhookPayloadParser, WebhookDownloadException (Batch 024)
│   │   ├── Integrations/Notifications/, Workflow/, Compliance/, InvoiceRecon/  # Ecosystem DTOs + exceptions (Batch 023)
│   │   ├── Observability/           # SentryPrivacyFilter (zero Sentry-SDK deps) (Batch 025)
│   │   ├── Services/                # Business logic, state machine, scanner core, analytics, extraction, diff, conflict detection
│   │   ├── Pagination/              # PaginationCursor, PageRequest, PagedResult<T>, IHasCursor
│   │   └── Validation/              # FluentValidation validators
│   ├── ContractEngine.Infrastructure/ # External concerns — DB, storage, tenancy, analytics, ecosystem clients
│   │   ├── Data/                    # ContractDbContext, migrations, FirstRunSeeder, HolidayCalendarSeeder, NotificationHubTemplateSeeder (Batch 025)
│   │   ├── Data/Migrations/         # 9 EF Core migrations (tenants → extraction_prompts_and_jobs)
│   │   ├── Repositories/            # EF Core repository implementations
│   │   ├── Storage/                 # LocalDocumentStorage
│   │   ├── Tenancy/                 # TenantContextAccessor (scoped ITenantContext writer)
│   │   ├── Analytics/               # EfAnalyticsQueryContext
│   │   ├── External/                # RagPlatformClient, NotificationHubClient, WorkflowEngineClient, ComplianceLedgerNatsPublisher, InvoiceReconClient, WebhookDocumentDownloader (typed HttpClients + resilience)
│   │   ├── Stubs/                  # No-op stubs for every ecosystem flag (RAG, Hub, Workflow, Compliance, Invoice Recon)
│   │   ├── Jobs/                    # DeadlineScanStore, DeadlineAlertWriter, AutoRenewalStore, StaleObligationStore
│   │   ├── Pagination/              # CursorPaginationExtensions
│   │   └── Configuration/           # ServiceRegistration.cs (DI, shared ecosystem resilience helper)
│   └── ContractEngine.Jobs/         # Quartz.NET background jobs
│       ├── DeadlineScannerJob.cs    # Hourly scanner (cron 0 0 * * * ?)
│       ├── ExtractionProcessorJob.cs # Every 5 min extraction processor (cron 0 */5 * * * ?)
│       ├── AutoRenewalMonitorJob.cs # Daily auto-renewal (cron 0 0 6 * * ?)
│       ├── StaleObligationCheckerJob.cs # Weekly stale check (cron 0 0 9 ? * MON)
│       └── ServiceRegistration.cs   # Quartz DI, JOBS_ENABLED gate
├── tests/
│   ├── ContractEngine.Core.Tests/          # Unit tests — domain logic (250 tests)
│   ├── ContractEngine.Api.Tests/           # In-process API tests via WebApplicationFactory (154)
│   ├── ContractEngine.Integration.Tests/   # Real DB + ecosystem stubs via DatabaseFixture (138)
│   └── ContractEngine.E2E.Tests/           # Real Kestrel subprocess HTTP tests (12)
├── docs/
│   ├── contract-lifecycle-engine_prd.md
│   ├── progress.md
│   └── operations/
│       ├── webhook-engine-setup.md    # Batch 024 — HMAC, payload shapes, idempotency, rotation
│       ├── betterstack-setup.md       # Batch 025 — uptime monitors, alerts, status page
│       └── notification-hub-setup.md  # Batch 025 — --seed-hub-templates procedure
├── ContractEngine.sln
├── Dockerfile                        # Multi-stage — sdk:8.0 build → aspnet:8.0 runtime, 334 MB
├── docker-compose.yml                # Dev — Postgres 16 on 5445, NATS on 4225 (profile)
├── docker-compose.override.yml       # git-ignored, per-developer port remap
├── docker-compose.prod.yml           # Prod — GHCR image, Caddy reverse proxy labels (pre-Batch-026 shape; CI/CD + VPS deploy OUT OF SCOPE)
├── .dockerignore                     # Excludes tests/, bin/, obj/ from build context
└── .env.example                      # Committed env var catalogue
```

**Test totals (Phase 3 close, HEAD = 14b246b): 714 non-E2E passing (Core 313 + Api 182 + Integration 219). E2E: 12 test classes exist across ports 5050–5062 including `WebhookEndpointE2ETests` (port 5062, Batch 024). `dotnet test --no-build --filter "FullyQualifiedName!~E2E"` confirms 714/714 passing.**

## Key Modules

| Module | Purpose | Key Files |
|--------|---------|-----------|
| Tenant Management | Multi-tenant isolation, API key auth, registration, self-serve profile | `src/ContractEngine.Core/Services/TenantService.cs`, `Api/Middleware/TenantResolutionMiddleware.cs` |
| Counterparty Management | Contract counterparty CRUD + search | `src/ContractEngine.Core/Services/CounterpartyService.cs` |
| Contract Management | CRUD + lifecycle (draft → active → terminated/archived), auto-counterparty | `src/ContractEngine.Core/Services/ContractService.cs` (CRUD + ecosystem wiring), `ContractService.Lifecycle.cs` (Activate/Terminate/Archive partial), `ContractRequests.cs` (request records) |
| Contract Documents | Multipart upload, local file storage, download streaming | `src/ContractEngine.Core/Services/ContractDocumentService.cs`, `Infrastructure/Storage/LocalDocumentStorage.cs` |
| Contract Tags & Versions | Tag replacement (REPLACE semantics), version history | `src/ContractEngine.Core/Services/ContractTagService.cs`, `ContractVersionService.cs` |
| Obligation Tracking | State machine, event sourcing, recurrence spawn, archive cascade | `src/ContractEngine.Core/Services/ObligationStateMachine.cs`, `ObligationService.cs` |
| Business Day Calculator | Holiday-aware business day math (US/DE/UK/NL) | `src/ContractEngine.Core/Services/BusinessDayCalculator.cs` |
| Deadline Alert Engine | Idempotent alert creation, bulk acknowledge | `src/ContractEngine.Core/Services/DeadlineAlertService.cs` |
| Deadline Scanner | Hourly Quartz job — auto-transition + alerts | `src/ContractEngine.Core/Services/DeadlineScannerCore.cs`, `Jobs/DeadlineScannerJob.cs` |
| Analytics | Dashboard + 3 aggregations (by type, value, calendar) | `src/ContractEngine.Core/Services/AnalyticsService.cs` |
| Health | ASP.NET + DB + integration readiness probes | `src/ContractEngine.Api/Endpoints/HealthEndpoints.cs` |
| Extraction Pipeline | AI-powered obligation extraction via RAG Platform | `src/ContractEngine.Core/Services/ExtractionService.cs` (orchestration), `ExtractionService.Pipeline.cs` (RAG upload + execute loop partial), `ExtractionResultParser.cs` (JSON → Obligation rows), `Jobs/ExtractionProcessorJob.cs` |
| Contract Analysis | Semantic diff, cross-contract conflict detection | `src/ContractEngine.Core/Services/ContractDiffService.cs`, `ConflictDetectionService.cs` |
| Auto-Renewal Monitor | Daily scan for expiring auto-renewal contracts | `src/ContractEngine.Core/Services/AutoRenewalMonitorCore.cs`, `Jobs/AutoRenewalMonitorJob.cs` |
| Stale Obligation Checker | Weekly scan for stale obligations the scanner missed | `src/ContractEngine.Core/Services/StaleObligationCheckerCore.cs`, `Jobs/StaleObligationCheckerJob.cs` |
| Ecosystem Integration | HTTP clients + NATS publisher for 6 ecosystem services (5 outbound: RAG, Notification Hub, Workflow, Compliance Ledger, Invoice Recon — Batches 019/023; 1 inbound: Webhook Engine ingestion — Batch 024) | **Phase 3** (`src/ContractEngine.Infrastructure/External/`) |
| Webhook Ingestion (inbound) | HMAC-verified `POST /api/webhooks/contract-signed` — normalises DocuSign/PandaDoc payloads, creates Draft contract, downloads signed PDF, triggers extraction. Idempotent on `envelope_id`/`document_id`. | `src/ContractEngine.Api/Endpoints/WebhookEndpoints.cs` (route + HMAC gate, slim), `WebhookEndpointHelpers.cs` (phase helpers: tenant resolve → idempotency probe → draft create → fire-and-forget chain), `src/ContractEngine.Core/Integrations/Webhooks/WebhookPayloadParser.cs`, `src/ContractEngine.Infrastructure/External/WebhookDocumentDownloader.cs` |

## Database Schema (Phase 2 — 12 tables, 9 migrations applied)

| Table | Migration | Purpose | Key Columns / Constraints |
|-------|-----------|---------|---------------------------|
| tenants | 20260416095140_InitialTenantsTable | Multi-tenant isolation | `id (uuid, gen_random_uuid())`, `api_key_hash UNIQUE`, `api_key_prefix`, `default_timezone`, `default_currency`, `is_active`, `metadata jsonb`. UNIQUE `(api_key_hash)`. |
| counterparties | 20260416103411_AddCounterpartiesTable | Contract counterparty companies | `id`, `tenant_id` FK, `name`, `legal_name`, `industry`, `contact_email`, `contact_name`, `notes`. Index `(tenant_id, name)`. |
| contracts | 20260416105029_AddContractsTable | Core contract records with lifecycle status | `id`, `tenant_id` FK, `counterparty_id` FK, `title`, `reference_number`, `contract_type`, `status`, `effective_date`, `end_date`, `auto_renewal`, `auto_renewal_period_months`, `total_value`, `currency`, `governing_law`, `rag_document_id`, `current_version`, `metadata jsonb`. Indexes `(tenant_id, status)`, `(tenant_id, counterparty_id)`, `(tenant_id, end_date)`, `(tenant_id, reference_number)`. |
| contract_documents | 20260416113211_AddContractDocumentsTable | Uploaded contract files (local FS + RAG handle) | `id`, `tenant_id`, `contract_id` FK, `version_number` (nullable — null = original), `file_name`, `file_path` (relative), `file_size_bytes`, `mime_type`, `rag_document_id`, `uploaded_at` (cursor column), `uploaded_by`. Index `(tenant_id, contract_id)`. |
| contract_tags | 20260416114932_AddContractTagsAndVersionsTables | Tagging system | `id`, `tenant_id`, `contract_id` FK, `tag varchar(100)`, `created_at`. UNIQUE `(tenant_id, contract_id, tag)`. Lookup index `(tenant_id, tag)`. |
| contract_versions | 20260416114932_AddContractTagsAndVersionsTables | Version history with semantic diff (JSONB) | `id`, `tenant_id`, `contract_id` FK, `version_number`, `change_summary text`, `diff_result jsonb`, `effective_date`, `created_by`, `created_at`. UNIQUE `(contract_id, version_number)`. Index `(tenant_id, contract_id, version_number)`. |
| obligations | 20260416121650_AddObligationsAndEvents | Tracked contractual obligations | `id`, `tenant_id`, `contract_id` FK (Restrict), `title`, `description`, `obligation_type`, `status`, `responsible_party`, `deadline_date`, `deadline_formula`, `recurrence`, `next_due_date`, `amount`, `currency`, `alert_window_days`, `grace_period_days`, `business_day_calendar`, `source`, `extraction_job_id` (uuid, no FK until Phase 2), `confidence_score`, `clause_reference`, `metadata jsonb`. Indexes `(tenant_id, status)`, `(tenant_id, contract_id)`, `(tenant_id, next_due_date)`, `(tenant_id, obligation_type)`. |
| obligation_events | 20260416121650_AddObligationsAndEvents | Immutable event-sourced status history | `id`, `tenant_id`, `obligation_id` FK (Cascade), `from_status` (raw string), `to_status` (raw string), `actor`, `reason`, `metadata jsonb`, `created_at`. **INSERT-ONLY** — interface enforces no Update/Delete (reflection test guards it). Index `(tenant_id, obligation_id, created_at)`. |
| holiday_calendars | 20260416141645_AddHolidayCalendarsTable | Business day calendar data (US/DE/UK/NL + tenant custom) | `id`, `tenant_id` (NULLABLE — null = system-wide), `calendar_code`, `holiday_date`, `holiday_name`, `year`, `created_at`. UNIQUE `(tenant_id, calendar_code, holiday_date)` WITH `NULLS NOT DISTINCT` (Postgres 15+). Indexes `(calendar_code, year, holiday_date)`, `(tenant_id, calendar_code)`. |
| deadline_alerts | 20260416143626_AddDeadlineAlertsTable | Proactive deadline and expiry alerts | `id`, `tenant_id`, `obligation_id` FK (Restrict), `contract_id` FK (Restrict), `alert_type`, `days_remaining`, `message`, `acknowledged`, `acknowledged_at`, `acknowledged_by`, `notification_sent`, `created_at`. Idempotency key `(obligation_id, alert_type, days_remaining)` enforced at the service layer. Indexes `(tenant_id, acknowledged, created_at DESC)`, `(tenant_id, obligation_id)`. |

| extraction_prompts | 20260417070833_AddExtractionPromptsAndJobsTables | Configurable extraction prompts per tenant/system-wide | `id`, `tenant_id` (NULLABLE — null = system-default), `prompt_type`, `prompt_text`, `response_schema jsonb`, `is_active`, `created_at`, `updated_at`. UNIQUE `(tenant_id, prompt_type)` NULLS NOT DISTINCT. NOT `ITenantScoped` (null rows are system-wide; repo handles isolation). |
| extraction_jobs | 20260417070833_AddExtractionPromptsAndJobsTables | AI extraction job tracking | `id`, `tenant_id`, `contract_id` FK (Restrict), `document_id` (nullable), `status`, `prompt_types text[]`, `obligations_found`, `obligations_confirmed`, `error_message`, `rag_document_id`, `raw_responses jsonb`, `started_at`, `completed_at`, `created_at`, `retry_count`. Indexes `(tenant_id, status)`, `(tenant_id, contract_id)`. |

## External Integrations

| Service | Purpose | Auth Method | Phase |
|---------|---------|------------|-------|
| Multi-Agent RAG Platform | AI-powered obligation extraction, semantic diff | X-API-Key via `RAG_PLATFORM_API_KEY` | Phase 2 (SHIPPED, Batch 019) |
| Event-Driven Notification Hub | Email/Telegram deadline alerts | X-API-Key via `NOTIFICATION_HUB_API_KEY` | Phase 3 (SHIPPED, Batch 023 + template seeder Batch 025) |
| Webhook Ingestion Engine | DocuSign/PandaDoc signed contract ingestion | HMAC-SHA256 via `WEBHOOK_SIGNING_SECRET` | Phase 3 (SHIPPED, Batch 024 — inbound `POST /api/webhooks/contract-signed`) |
| Workflow Automation Engine | Contract amendment approval workflows | X-API-Key via `WORKFLOW_ENGINE_API_KEY` | Phase 3 (SHIPPED — client only, Batch 023; call-site wiring deferred) |
| Financial Compliance Ledger | Regulatory audit trail via NATS JetStream | NATS connection (no auth) | Phase 3 (SHIPPED, Batch 023) |
| Invoice Reconciliation Engine | Auto-create POs from payment obligations | X-API-Key via `INVOICE_RECON_API_KEY` | Phase 3 (SHIPPED, Batch 023) |

## Ecosystem Connections

| Direction | System | Method | Env Vars |
|-----------|--------|--------|----------|
| this → | RAG Platform | REST (POST /api/documents, /api/search, /api/chat/sync) | RAG_PLATFORM_URL, RAG_PLATFORM_API_KEY, RAG_PLATFORM_ENABLED |
| this → | Notification Hub | REST (POST /api/events) | NOTIFICATION_HUB_URL, NOTIFICATION_HUB_API_KEY, NOTIFICATION_HUB_ENABLED |
| ← this | Webhook Engine | REST inbound (POST /api/webhooks/contract-signed) | WEBHOOK_SIGNING_SECRET, WEBHOOK_ENGINE_ENABLED |
| this → | Workflow Engine | REST (POST /webhooks/{path}) | WORKFLOW_ENGINE_URL, WORKFLOW_ENGINE_API_KEY, WORKFLOW_ENGINE_ENABLED |
| this → | Compliance Ledger | NATS JetStream (contract.obligation.breached, contract.renewed, contract.terminated) | NATS_URL, COMPLIANCE_LEDGER_ENABLED |
| this → | Invoice Recon | REST (POST /api/purchase-orders) | INVOICE_RECON_URL, INVOICE_RECON_API_KEY, INVOICE_RECON_ENABLED |

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| DATABASE_URL | PostgreSQL connection string (Npgsql format) | `Host=localhost;Port=5445;...` |
| PORT | API port | `5000` |
| ASPNETCORE_ENVIRONMENT | `Development` or `Production` | `Development` |
| AUTO_MIGRATE | Apply pending EF Core migrations on startup. Set `false` for CI-triggered manual migration pipelines. Short-circuits when `ASPNETCORE_ENVIRONMENT=Testing`. | `true` |
| AUTO_SEED | Auto-run seed on first boot (holiday calendars + default tenant) | `true` |
| JOBS_ENABLED | Wire Quartz scheduler + `DeadlineScannerJob`. Set `false` in test factories and E2E subprocesses to avoid a hosted scheduler thread. | `true` |
| SELF_REGISTRATION_ENABLED | Allow public `POST /api/tenants/register` | `true` |
| DEFAULT_TENANT_NAME | Name of auto-seeded first tenant | `Default` |
| DOCUMENT_STORAGE_PATH | Local path for uploaded contract files | `data/documents` |
| EXTRACTION_BATCH_SIZE | Max extraction jobs per scheduler tick (Phase 2) | `5` |
| EXTRACTION_RETRY_MAX | Max retries for failed extraction jobs (Phase 2) | `3` |
| ALERT_WINDOWS_DAYS | CSV of days before deadline to alert | `90,30,14,7,1` |
| DEFAULT_GRACE_PERIOD_DAYS | Business days of grace after deadline | `3` |
| OVERDUE_ESCALATION_DAYS | Business days overdue before escalation | `14` |
| DEFAULT_RENEWAL_NOTICE_DAYS | Days before `end_date` to mark a contract `Expiring` | `90` |
| RAG_PLATFORM_URL / _API_KEY / _ENABLED | RAG Platform connection (Phase 2) | `_ENABLED=false` locally |
| NOTIFICATION_HUB_URL / _API_KEY / _ENABLED | Notification Hub connection (Phase 3) | `_ENABLED=false` |
| WEBHOOK_SIGNING_SECRET / WEBHOOK_ENGINE_ENABLED | Webhook signature verification (Phase 3) | `_ENABLED=false` |
| WORKFLOW_ENGINE_URL / _API_KEY / _ENABLED | Workflow Engine connection (Phase 3) | `_ENABLED=false` |
| NATS_URL / COMPLIANCE_LEDGER_ENABLED | NATS JetStream for Compliance Ledger (Phase 3) | `_ENABLED=false` |
| INVOICE_RECON_URL / _API_KEY / _ENABLED | Invoice Recon connection (Phase 3) | `_ENABLED=false` |
| SENTRY_DSN | Sentry error tracking DSN (empty = disabled) | empty |
| LOG_LEVEL | Serilog min level: Verbose / Debug / Information / Warning / Error | `Information` |

Values live in `.env` locally (git-ignored) and in environment variables / secrets in production. `.env.example` is the committed catalogue.

## Commands

| Action | Command |
|--------|---------|
| Dev server | `dotnet run --project src/ContractEngine.Api` |
| Run tests | `dotnet test` |
| Lint/check | `dotnet build --no-incremental` |
| Build | `dotnet build` |
| Migrate DB | `dotnet ef database update --project src/ContractEngine.Infrastructure --startup-project src/ContractEngine.Api` |
| Add migration | `dotnet ef migrations add <Name> --project src/ContractEngine.Infrastructure --startup-project src/ContractEngine.Api` |
| Seed data | `dotnet run --project src/ContractEngine.Api -- --seed` |
| E2E tests | `dotnet test tests/ContractEngine.E2E.Tests/` |
| Docker build | `docker build -t contract-lifecycle-engine .` |
| Docker compose (dev) | `docker compose up -d` |
| Docker compose (prod config check) | `docker compose -f docker-compose.prod.yml config` |

## Tenant Model

- **Isolation:** API key per tenant, resolved via `X-API-Key` header
- **Scoping:** `tenant_id` on every data table, enforced by EF Core global query filter (for entities implementing `ITenantScoped`)
- **Key Format:** `cle_live_{32_hex_chars}` — SHA-256 hashed for storage
- **Middleware:** `TenantResolutionMiddleware` resolves key → tenant → scoped `TenantContextAccessor`
- **Registration:** `POST /api/tenants/register` (guarded by `SELF_REGISTRATION_ENABLED`)
- **Unresolved requests:** Middleware leaves context unresolved WITHOUT rejecting — public endpoints stay accessible; rejection is the endpoint's concern via `ITenantContext.IsResolved`.

## Key Patterns & Conventions

### Architectural Principles (always on)

- **Minimal APIs:** Endpoint groups in `Endpoints/` classes, not MVC controllers. Keep handlers thin — validate, call a service, format the response.
- **Clean architecture:** `Core` (domain) → `Infrastructure` (data/external) → `Api` (composition root) → `Jobs` (scheduled work). Never import upward. `Core` has ZERO external dependencies.
- **Feature-flagged integrations:** Each ecosystem service behind `{SERVICE}_ENABLED` env var. Real client or no-op stub registered at startup (Phase 2 / 3).
- **Event sourcing (obligations):** Every status change → immutable `obligation_events` insert. No UPDATE/DELETE on events table.
- **Extract-then-confirm:** AI-extracted obligations always `pending` until human confirms. No auto-activation.
- **Async/Await:** All I/O operations async. Methods suffixed with `Async`.
- **Import order:** `System.*` → third-party packages → local project namespaces, blank line between groups.

### 1. Error Response Envelope (PRD Section 8b)

Every non-2xx response emitted by the API MUST use this shape — no exceptions, including for 404s and 422 validation errors. The shape is enforced by `ExceptionHandlingMiddleware`; service code throws typed exceptions (`ValidationException`, `KeyNotFoundException`, `UnauthorizedAccessException`, `EntityTransitionException`, etc.) and the middleware serialises them.

```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "Human-readable description of what went wrong",
    "details": [
      { "field": "end_date", "message": "Must be after effective_date" }
    ],
    "request_id": "req_abc123"
  }
}
```

Allowed codes: `VALIDATION_ERROR`, `NOT_FOUND`, `CONFLICT`, `INVALID_TRANSITION`, `UNAUTHORIZED`, `FORBIDDEN`, `RATE_LIMITED`, `INTERNAL_ERROR`, `SERVICE_UNAVAILABLE`. `details` is optional (omit for 404, required for 422). `request_id` is minted per-request (maps to `HttpContext.TraceIdentifier`) and echoed into logs and responses.

### 2. Cursor-Based Pagination (PRD Section 8b)

All list endpoints use **composite cursors** built from `(created_at, id)`, base64-encoded. No page numbers, no offset. Default page size **25**, max **100**. The shared helper `PaginationCursor` (Core) encodes/decodes, and `CursorPaginationExtensions.ApplyCursorAsync` (Infrastructure) applies the WHERE clause plus fetches `PageSize + 1` rows to detect `HasMore` in one round-trip.

Query parameters: `?cursor=<opaque>&page_size=<1-100>`.

Response envelope:

```json
{
  "data": [ /* items */ ],
  "pagination": {
    "next_cursor": "eyJjcmVhdGVkQXQiOiIyMDI2LTA0LTE1VDEwOjAwOjAwWiIsImlkIjoiLi4uIn0=",
    "has_more": true,
    "total_count": 142
  }
}
```

`total_count` is optional (expensive on large tables) — omit on hot-path list endpoints and include on admin/analytics endpoints.

### 3. Tenant Resolution & Isolation (PRD Section 5.1, 8b)

Every non-public request flows through this pipeline:

1. **`TenantResolutionMiddleware`** reads the `X-API-Key` header.
2. Key format is `cle_live_{32_hex_chars}`. Malformed keys → unresolved context (NOT 401 at the middleware — the endpoint decides).
3. Compute `SHA-256(apiKey)` → hex string.
4. Query `tenants` WHERE `api_key_hash = @hash AND is_active = true` (via `IgnoreQueryFilters()` since the context isn't yet resolved).
5. On match, call `TenantContextAccessor.Resolve(tenantId)` — same instance is aliased as `ITenantContext` and `TenantContextAccessor` so downstream DI resolves the resolved context.
6. All downstream services and the `DbContext` read from `ITenantContext`.

**Public endpoints** (no key required): `POST /api/tenants/register` (when `SELF_REGISTRATION_ENABLED=true`), `GET /health`, `GET /health/db`, `GET /health/ready`, `POST /api/webhooks/contract-signed` (HMAC-verified separately — Phase 3).

### 4. EF Core Global Query Filter for Tenant Isolation (PRD Section 5.1)

Every entity implementing the `ITenantScoped` marker interface gets a global query filter registered in `ContractDbContext.ApplyTenantQueryFilter<T>`:

```csharp
Expression<Func<TEntity, bool>> filter = entity =>
    _tenantContext.TenantId != null && entity.TenantId == _tenantContext.TenantId;
builder.Entity<TEntity>().HasQueryFilter(filter);
```

This ensures `context.Contracts.ToList()` already filters to the current tenant — no hand-written `.Where(c => c.TenantId == ...)` in services. For jobs (`DeadlineScannerJob`) and seed scripts that need cross-tenant access, repositories call `IgnoreQueryFilters()` explicitly (`TenantRepository`, `DeadlineScanStore`, `HolidayCalendarRepository`).

Entities with `ITenantScoped`: `Counterparty`, `Contract`, `ContractVersion`, `ContractDocument`, `ContractTag`, `Obligation`, `ObligationEvent`, `DeadlineAlert`. Explicitly NOT scoped: `Tenant` (trivially tenant-self), `HolidayCalendar` (nullable tenant_id → filter would hide system-wide rows; repo enforces isolation explicitly).

### 5. FluentValidation Pipeline Integration (PRD Section 3, 5.1)

All request DTOs have a matching `AbstractValidator<T>` in `ContractEngine.Core/Validation/`. Validators are auto-registered by assembly scan in `ServiceRegistration.cs`:

```csharp
services.AddValidatorsFromAssemblyContaining<RegisterTenantRequestValidator>();
```

Minimal-API endpoints resolve `IValidator<TRequest>` manually in the handler (the per-endpoint filter pattern is documented but validators currently run inline); on failure the handler throws a `ValidationException` carrying the field errors. `ExceptionHandlingMiddleware` converts that to the `VALIDATION_ERROR` envelope above.

### 6. Serilog Structured JSON Logging (PRD Section 9, 10b)

Single sink: stdout, JSON formatter (`CompactJsonFormatter`). Configured at startup in `Program.cs` via `Host.UseSerilog((ctx, svc, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console(new CompactJsonFormatter()).Enrich.FromLogContext())`. Min level from `LOG_LEVEL` env var.

**Mandatory enrichment** (added by `RequestLoggingMiddleware` via `Serilog.Context.LogContext.PushProperty`):

- `request_id` — same value returned in the error envelope; `HttpContext.TraceIdentifier`
- `tenant_id` — resolved by `TenantResolutionMiddleware`; null for public endpoints
- `module` — logical area derived from `/api/{module}` path or first path segment; default `http`
- `environment` — `Development` / `Production`

**Never log:** raw API keys, `X-API-Key` header values, signed JWTs, counterparty contact emails, contract full text, uploaded document binary.

**Do log:** business events, scheduled job outcomes (`deadline_scanner.*`), request latency, exceptions with stack traces.

**Test awareness:** `Program.cs` detects a test-supplied static `Log.Logger` (any type != `SilentLogger`) and, in that case, skips the bootstrap logger replacement AND uses `builder.Host.UseSerilog(Log.Logger, dispose: false)` instead of the reloadable DI-resolved callback — preserves test-supplied `InMemoryLogSink` instances across multiple `WebApplicationFactory<Program>` subclasses in the same assembly.

### 7. Sentry Error Tracking (Batch 025, PRD Section 10b)

Added in Batch 025. Sentry is wired in `Program.cs` via `builder.WebHost.UseSentry(...)` and gated on a non-empty `SENTRY_DSN` — empty DSN → Sentry silently disabled (local dev and test harnesses never touch the network). On non-empty DSN:

- **Source 1:** ASP.NET Core middleware exceptions captured by `UseSentry()`.
- **Source 2:** Serilog events of `Error` / `Fatal` level forwarded via the `Sentry.Serilog` sink (same DSN, configured inside the `UseSerilog` callback).
- **TracesSampleRate:** `0.1` (10% of request traces; tune via code).
- **Environment / Release:** pulled from `builder.Environment.EnvironmentName` and the API assembly version.
- **PII scrubbing:** the `BeforeSend` callback copies `sentryEvent.Request.Headers` into a `Dictionary<string,string>`, calls `SentryPrivacyFilter.Scrub(...)`, and writes the scrubbed values back. Blocklist: `X-API-Key`, `X-Tenant-API-Key`, `X-Webhook-Signature`, `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization` plus any key containing `api_key` / `apikey` / `signature` / `password` / `secret` / `token` (case-insensitive, substring). Every sensitive value is replaced with the literal string `[REDACTED]` — the key stays so operators can still see the request shape.
- **Core has zero Sentry deps:** `SentryPrivacyFilter` lives in `ContractEngine.Core/Observability/` and operates on plain `IDictionary` types so the scrubbing logic is fully unit-testable without importing `Sentry`. The Program.cs adapter translates SDK types into dictionaries.

---

- **Business day math:** `BusinessDayCalculator` uses holiday calendars with timezone-aware dates. 24 h in-memory cache keyed on `"holidays::{code}::{year}::{tenantId?}"`. Stateless singleton backed by `IHolidayCalendarRepositoryFactory` for per-call scoped DbContext.

## Gotchas & Lessons Learned

> Discovered during implementation. Added automatically by `/implement-next` Step 9.3.

| Date | Area | Gotcha | Discovered In |
|------|------|--------|---------------|
| 2026-04-04 | PostgreSQL/Docker | Local port 5432 may conflict with system PostgreSQL. Map Docker to alternate port (e.g., 5440:5432) in docker-compose.override.yml. Update DATABASE_URL accordingly. | Swarm Intelligence Gateway |
| 2026-04-16 | Docker Compose | Sibling projects on this workstation occupy 5432, 5433, 5434, 5435, 5436, 5440, 5441, 5442, 5443, 5444, 5450 (Postgres) and 4222, 4223, 4224 (NATS). Contract Lifecycle Engine uses **5445** (Postgres) and **4225/8225** (NATS) via `docker-compose.override.yml`. Override uses the `!override` tag so port lists replace, not append. | Batch 001 |
| 2026-04-16 | Docker Compose | The `version: "3.8"` header is obsolete in Compose v2+ and emits a warning. Remove it from both `docker-compose.yml` and `docker-compose.override.yml` (and `docker-compose.prod.yml` as of Batch 018). | Batch 001 / 018 |
| 2026-04-16 | Platform | .NET 8 / C# 12 is the committed target (not .NET 9 / C# 13 as originally pinned in the PRD). The host SDK is 8.0.400; PRD, Dockerfile (`sdk:8.0` / `aspnet:8.0`), llms.txt, agent rules, workflows, and session-continuity files (CLAUDE.md / AGENTS.md / .cursorrules) all now say .NET 8. Always check host SDK before pinning a framework in a PRD. | Batch 002 |
| 2026-04-16 | Windows / subprocess | When launching a long-running .NET process with `RedirectStandardOutput` + `RedirectStandardError`, you MUST drain both streams via `BeginOutputReadLine` / `BeginErrorReadLine` **before** the poll loop starts, or the child stalls once the ~64KB Windows pipe buffer fills. Reading `StandardError` synchronously at the end does not help — Kestrel has already blocked by then. Pattern is codified in `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs`. | Batch 002 |
| 2026-04-16 | dotnet CLI | `dotnet add reference` does NOT support `--no-restore` (unlike `dotnet add package`). Omit the flag for project-reference adds; they're cheap and deferred-restored anyway. | Batch 002 |
| 2026-04-15 | WebApplicationFactory / Serilog | `Serilog.Sinks.TestCorrelator` uses an `AsyncLocal` correlation GUID that does NOT propagate reliably through `Microsoft.AspNetCore.TestHost.TestServer`'s in-process request pipeline — log events emitted inside the request handler land outside the test's `CreateContext` scope and are invisible to `GetLogEventsFromCurrentContext`. Use a process-wide `ILogEventSink` (e.g., a `ConcurrentQueue`-backed `InMemoryLogSink.Instance`) with `Clear()` at the start of each test and `Snapshot()` at the end instead. Pattern in `tests/ContractEngine.Api.Tests/Middleware/InMemoryLogSink.cs`. | Batch 003 |
| 2026-04-15 | Test project setup | In a `Microsoft.NET.Sdk` test project that uses `WebApplicationFactory<Program>`, ambiguous extension-method resolution between `Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.Configure(IWebHostBuilder, Action<IApplicationBuilder>)` and `Microsoft.Extensions.DependencyInjection.SocketsHttpHandlerBuilderExtensions.Configure` causes CS1929. Fix: (a) add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to the test `.csproj`, AND (b) call the extensions using their fully-qualified static-class names (`Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.Configure(builder, ...)`, `Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(builder, ...)`, `Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, ...)`). | Batch 003 |
| 2026-04-15 | .NET 8 Logging | `Program.cs` calls `builder.Host.UseSerilog((ctx, svc, cfg) => ...)` which replaces `ILoggerFactory` in DI. Tests that want to inject their own sink must override in `WebApplicationFactory.CreateHost` AFTER `base.CreateHost(builder)` pre-configures — call `builder.UseSerilog(Log.Logger, dispose: false)` and ensure the static `Log.Logger` is set (preferably in the factory's static ctor) with the desired sink BEFORE the Program `Host.UseSerilog` callback evaluates. | Batch 003 |
| 2026-04-16 | Serilog bootstrap + tests | `Log.Logger = …CreateBootstrapLogger()` at the top of `Program.cs` fires on EVERY `WebApplicationFactory<Program>` host boot and CLOBBERS any test-supplied sink that was installed in a factory's static ctor. Symptom: the first suite that pre-seeds a sink runs clean in isolation; once a second factory runs (e.g. `TenantResolutionTestFactory` alongside `RequestLoggingTestFactory`), the sink-dependent assertions flip to empty collections. Fix: guard the bootstrap assignment with a `Log.Logger.GetType().Name == "SilentLogger"` check so Program only seeds a logger when none is already installed. | Batch 004 |
| 2026-04-16 | Multi-factory Serilog | When two or more `WebApplicationFactory<Program>` subclasses live in the same test assembly, each of them must install a non-reloadable logger in a static ctor AND override `CreateHost` to call `builder.UseSerilog(Log.Logger, dispose: false)`. Without the `CreateHost` override, the second factory trips `InvalidOperationException: The logger is already frozen.` coming from `Serilog.Extensions.Hosting.ReloadableLogger.Freeze()`. A shared `SerilogTestBootstrap.EnsureInitialized()` helper keeps it idempotent and respects a pre-installed sink (idempotently skips if the current logger isn't `SilentLogger`). | Batch 004 |
| 2026-04-16 | EF Core migrations | `dotnet ef` requires `Microsoft.EntityFrameworkCore.Design` on the **startup project**, not just the migrations project. Add it to `src/ContractEngine.Api/ContractEngine.Api.csproj` with `<PrivateAssets>all</PrivateAssets>` so it does not flow to downstream consumers. `dotnet-ef` tool itself installs globally via `dotnet tool install --global dotnet-ef --version 8.0.11`. | Batch 004 |
| 2026-04-16 | FluentValidation DI | `services.AddValidatorsFromAssemblyContaining<T>()` lives in `FluentValidation.DependencyInjectionExtensions` (separate NuGet from base `FluentValidation`). Add it to Infrastructure where the DI registration lives. Assembly-scan the Core validators so future additions are picked up automatically. | Batch 004 |
| 2026-04-16 | PostgreSQL schema | `gen_random_uuid()` is built-in from Postgres 13+, BUT always emit a defensive `migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");` at the top of the first migration. It's a no-op on 13+ and covers any dev who runs Postgres 12. JSONB columns mapped via `HasConversion` emit a warning about missing value comparers — harmless for non-query JSONB payloads. | Batch 004 |
| 2026-04-16 | Tenant context DI | Keep the resolved tenant behind an immutable `ITenantContext` surface while giving middleware a mutable writer via a separate `TenantContextAccessor` class. Register once as scoped and alias `ITenantContext` to the accessor: `services.AddScoped<TenantContextAccessor>(); services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>());` — the SAME instance is resolved for both types per request. Do not register `NullTenantContext` as the default anymore; the accessor reports `IsResolved=false` naturally before the middleware calls `Resolve`. | Batch 004 |
| 2026-04-16 | xUnit parallelism + Serilog | When an assembly contains multiple `WebApplicationFactory<Program>` subclasses (e.g. `RequestLoggingTestFactory`, `TenantResolutionTestFactory`, `TenantEndpointsTestFactory`, `TenantEndpointsDisabledFactory`, `ExceptionHandlingTestFactory`), xUnit's default test-class parallelism races on the process-global `Serilog.Log.Logger`. Symptom: sink-based assertions see empty event queues because another factory's Program boot has swapped `Log.Logger` mid-test. Fix: define a `[CollectionDefinition("WebApplication", DisableParallelization = true)]` and tag every factory-using test class with `[Collection("WebApplication")]`. The collection runs serially while OTHER (non-factory) test classes in the assembly still parallelise normally. | Batch 004 |
| 2026-04-16 | Program.cs test-awareness | `Program.cs` detects a test-supplied static logger (`Log.Logger.GetType().Name != "SilentLogger"`) and, in that case, replaces `builder.Host.UseSerilog((ctx, svc, cfg) => ...)` (which queues a ReloadableLogger via DI and dies with "already frozen" on the second factory) with `builder.Host.UseSerilog(Log.Logger, dispose: false)` (which just forwards the pre-supplied logger). Production paths are unchanged — Log.Logger is always SilentLogger on first Program startup. | Batch 004 |
| 2026-04-16 | gitignore shadowing | Windows filesystems are case-insensitive; a gitignore pattern like `data/` can accidentally shadow BOTH `data/documents/` AND a source directory named `Data/` if someone creates one. When adding directories under `src/*/Data/` for EF Core migrations, double-check `git status --ignored` shows the intended entries tracked, not ignored. | Batch 004 |
| 2026-04-16 | Rate limiter naming | `FixedWindowRateLimiterOptions` in .NET 8 uses `PermitLimit` / `Window` (not `PermitCount` / `WindowDuration` — those are from a different preview API). Mismatching the property names compiles against the wrong overload and silently applies default limits at runtime. Verify property names against `System.Threading.RateLimiting` 8.0.x when wiring policies. | Batch 005 |
| 2026-04-16 | Postgres UNIQUE with nullable | PRD §4.10 requires UNIQUE `(tenant_id, calendar_code, holiday_date)` but `tenant_id` is NULLABLE (system-wide rows). By default Postgres treats NULL != NULL, so two system-wide rows for the same `(calendar_code, holiday_date)` would both insert. Fix: Postgres 15+ `CREATE UNIQUE INDEX ... NULLS NOT DISTINCT`. Emit the raw SQL in the migration; EF Core 8's `HasIndex().IsUnique()` does NOT generate `NULLS NOT DISTINCT` automatically. | Batch 014 |
| 2026-04-16 | DI ctor selection | When a Core service has two public constructors (production + test-only factory path), MS.DI `ValidateOnBuild` fails with "multiple constructors" even if only one matches registered dependencies. Workaround: keep a single public ctor and expose a `static ForTesting(...)` factory that returns an instance via reflection or a private ctor. `BusinessDayCalculator` uses this pattern. | Batch 014 |
| 2026-04-16 | Business-day asymmetry | `BusinessDaysUntil(DateOnly.Today.AddDays(-N))` is NOT guaranteed to return `-N` even for pure calendar math — if `N` spans a weekend, the business-day count is smaller than the calendar distance. Tests that want `-7` must seed past dates via `BusinessDaysAfter(today, -7, "US")` (or accept the directional count). | Batch 014 |
| 2026-04-16 | Minimal API IFormFile + empty multipart | `[FromForm] IFormFile? file` on a Minimal API handler throws a 500 when the caller posts a multipart body with no file part. Workaround: read the form manually via `httpContext.Request.ReadFormAsync()` + `form.Files.GetFile("file")`, then raise a `ValidationException` when the file is missing so the middleware maps to 400 VALIDATION_ERROR. Also call `.DisableAntiforgery()` on the upload route (or antiforgery-enabled pipelines) so the binder doesn't demand a token the SDK never sends. | Batch 009 |
| 2026-04-16 | E2E port allocation | Each E2E test class binds a dedicated port (5050, 5051, 5052, 5053, 5054, …). Two classes on the same port race on bind and the second one to start gets connection-refused intermittently. When adding a new E2E class, pick the next unused port above the current max (Phase 1 close: 5061). | Batch 009 |
| 2026-04-16 | Test DB migration drift | `dotnet ef database update` applied migrations to the dev `contract_engine` DB via `DATABASE_URL` from `.env`, but `contract_engine_test` (used by integration + API tests via the DatabaseFixture / factory `EnsureDatabaseReady` hooks) only catches up when the fixture actually runs. If you apply a migration then run `dotnet test` before the fixture hits `Migrate()`, the first test run will 500 on the new table. Retrying works (fixture migrates on first instantiation). Safer: run `DATABASE_URL='...contract_engine_test...' dotnet ef database update` explicitly when you see `relation "X" does not exist` from integration tests. | Batch 009 |
| 2026-04-16 | Quartz hosted scheduler in tests | `AddQuartz` + `AddQuartzHostedService` installs a background thread that fires triggers on the real clock. Every `WebApplicationFactory<Program>` instantiation and every E2E subprocess pays that cost (~500ms startup), and a trigger that fires mid-test attempts DB writes against the shared `contract_engine_test` DB → flaky transitions, duplicate alerts. Fix: every test-side factory MUST set `JOBS_ENABLED=false` (WAFs: `builder.UseSetting("JOBS_ENABLED", "false")`; E2E: `psi.Environment["JOBS_ENABLED"] = "false"`). `AUTO_SEED=false` goes alongside so first-boot also doesn't insert a default tenant. `ServiceRegistration.AddContractEngineJobs` respects the flag and skips the `AddQuartz` block entirely — unit tests resolving `IDeadlineScanStore` directly still work because scanner collaborators register unconditionally. | Batch 016 |
| 2026-04-16 | Scanner tenant context pattern | The hourly `DeadlineScannerJob` runs without a pre-resolved `ITenantContext` (it iterates every tenant's obligations in one sweep). Reads bypass the global query filter via `IgnoreQueryFilters()` on `DeadlineScanStore`. Writes that need tenant-scoped collaborators (`DeadlineAlertService` requires a resolved tenant) go through `DeadlineAlertWriter` which, per call, opens a child DI scope, resolves the scoped `TenantContextAccessor`, calls `Resolve(tenantId)`, runs the operation, disposes. In integration tests don't override `ITenantContext` with `NullTenantContext` for scanner paths — that breaks the `TenantContextAccessor`/`ITenantContext` alias so the write-side tenant is resolved on a different instance from the one the DbContext + service read. Use `_fixture.CreateScope()` (no override) instead; `IgnoreQueryFilters()` in the store handles the cross-tenant reads. | Batch 016 |
| 2026-04-16 | Scanner integration-test determinism | `BusinessDayCalculator.BusinessDaysUntil(calendarDate)` depends on the current wall clock AND the holiday calendar — seeding an obligation with `today.AddDays(7)` does NOT reliably yield 7 business days remaining (weekend or US holiday between today and then-ish will collapse 7 calendar days into fewer business days). When writing scanner integration tests that assert on exact alert windows, seed the obligation's `next_due_date` via `IBusinessDayCalculator.BusinessDaysAfter(today, N, "US")` so the inverse call returns the exact N you want. | Batch 016 |
| 2026-04-16 | Docker build context / solution restore | `ContractEngine.sln` in the build context causes a bare `dotnet restore` (no project arg) to attempt the whole solution — including `tests/**/*.csproj` that the Dockerfile never copied. Symptom: `MSB3202: project file not found`. Fix: restore the Api project explicitly (`dotnet restore src/ContractEngine.Api/ContractEngine.Api.csproj`) so the project-refs pull Core/Infrastructure/Jobs transitively; add `tests/` to `.dockerignore` so the runtime image stays lean. Resulting image: 334 MB on `mcr.microsoft.com/dotnet/aspnet:8.0`. | Batch 018 |
| 2026-04-16 | AUTO_MIGRATE default + test factories | `Program.cs` runs `Database.MigrateAsync()` on startup when `AUTO_MIGRATE=true` (default). Test factories using `WebApplicationFactory<Program>` must set `builder.UseSetting("AUTO_MIGRATE", "false")` alongside the existing `AUTO_SEED=false` and `JOBS_ENABLED=false` flags — otherwise every factory instantiation races on the shared `contract_engine_test` schema. A secondary guard is the `app.Environment.IsEnvironment("Testing")` short-circuit but tests use `Development` / `Production` for real environment coverage, so the config flag is the primary opt-out. | Batch 018 |
| 2026-04-17 | NATS.Client v1 vs v2 APIs | The `NATS.Client` 1.1.8 package uses the v1 API: `IConnection.Publish(subject, byte[])` and `IConnection.Flush(int milliseconds)` — NOT `Flush(TimeSpan)` (that's v2). Passing a TimeSpan to `Flush()` fails to compile with CS1503 `TimeSpan → int`. Fix: `conn.Flush(5000)` for 5 seconds. Likewise, connection pattern matching: `IsClosed` is a **set-only** property in v1 so `is { IsClosed: false, State: ConnState.CONNECTED }` fails CS0154 — match only on `{ State: ConnState.CONNECTED }`. | Batch 023 |
| 2026-04-17 | Optional-ctor legacy-test pattern | When adding new DI collaborators to existing services (`DeadlineAlertService`, `ObligationService`, `ContractService`, `AutoRenewalMonitorCore`), treat them as OPTIONAL ctor params with a default of `null` and a private `NullXxxPublisher` / `NullXxxClient` fallback behind the null-coalescing. This keeps pre-Batch-023 unit tests (`new ObligationService(repo, eventRepo, tenantContext, stateMachine)`) compiling without forcing every test to learn the new DI surface. Production `AddContractEngineInfrastructure` always resolves a real implementation (either the live client or the no-op stub registered by the feature-flag branch), so the null fallback never fires at runtime. | Batch 023 |
| 2026-04-17 | Fire-and-forget side-effect semantics | Every Phase 3 ecosystem emission (Notification Hub publish, Compliance Ledger publish, Invoice Recon PO) runs AFTER the domain DB commit and is wrapped in `try { … } catch (Exception ex) { _logger.LogWarning(ex, …); }` — the catch swallows the exception so a missed notification or ledger entry never rolls back the transition that produced it. Don't move the emission inside the transaction scope thinking it's atomic; the ledger is a trailing audit stream and the PRD explicitly treats it as eventually consistent. The no-op stubs return not-dispatched without throwing, so the try/catch only matters when the real client is wired. | Batch 023 |
| 2026-04-17 | Shared ecosystem resilience helper | `ServiceRegistration.cs` now has a private `ConfigureEcosystemResilience(pipelineName)` helper that the four typed-HttpClient registrations (Notification Hub, Workflow Engine, Invoice Recon, and future ones) call to install the same retry-3× + circuit-breaker policy as the RAG client. When adding a NEW ecosystem HTTP client, do NOT copy-paste the resilience config — call the shared helper with a unique pipeline name. Drift between pipelines causes hard-to-debug retry-storm differences across services. | Batch 023 |
| 2026-04-17 | Raw body buffering for HMAC verification | `POST /api/webhooks/contract-signed` MUST call `httpContext.Request.EnableBuffering()` + `Body.Position = 0` BEFORE reading via `StreamReader` and AGAIN after reading, so the body stream is rewound for any downstream framework readers. The HMAC must be computed over the UTF-8 bytes of the raw body (not a re-serialised JSON string) — any reformatting between receipt and hashing breaks signature verification. Use `CryptographicOperations.FixedTimeEquals` on byte arrays (not string equals on hex) to avoid timing-attack leakage. | Batch 024 |
| 2026-04-17 | Webhook feature-flag 404 (not 403) | When `WEBHOOK_ENGINE_ENABLED=false` OR `WEBHOOK_SIGNING_SECRET` is blank, the webhook endpoint returns **404**, not 403. This mirrors the tenant-registration-disabled pattern: port scanners see no hint the endpoint exists. 403 would confirm "it's here but I'm not letting you in" — 404 claims "nothing here at all." Both failure modes collapse to one response so operators can disable a single host without advertising it. | Batch 024 |
| 2026-04-17 | Webhook idempotency via JSONB metadata | The webhook handler stamps every new Draft contract's `metadata` JSONB column with `webhook_envelope_id` (DocuSign) or `webhook_document_id` (PandaDoc). Redeliveries probe via `EF.Functions.JsonContains(c.Metadata, "{\"webhook_envelope_id\":\"<id>\"}")` — tenant-scoped by explicit `.Where(c => c.TenantId == tenantId)` (the global query filter is in play because the handler resolves the context first). Do NOT add a bespoke `IContractRepository.FindByWebhookExternalIdAsync` method — the endpoint queries `ContractDbContext.Contracts` directly to keep the webhook concern out of the repository's public surface. | Batch 024 |
| 2026-04-17 | Unsealing services for test decorator pattern | `ExtractionService` was originally `public sealed class` with non-virtual methods. Batch 024's `CountingExtractionService` test decorator (Api.Tests webhook factory) needed to override `TriggerExtractionAsync` to assert it was called exactly once after a webhook arrival. Fix: remove `sealed`, add `virtual` to the decorated method. This is a minimal-impact refactor — no functional change to production code, just enables test decoration. Apply the same pattern if future tests need to wrap an ecosystem emission in a counting decorator. | Batch 024 |
| 2026-04-17 | ContractType enum has no "Other" | When creating a Draft contract from a webhook payload (which doesn't carry contract-type info), defaulting to `ContractType.Vendor` is intentional — the enum has six values (`Vendor`, `Customer`, `Partnership`, `Nda`, `Employment`, `Lease`) with NO `Other` catch-all. Operators retype via PATCH after reviewing the draft. Do NOT add an `Other` value to the enum just to avoid picking a default — it would propagate into the PRD §4.3 CHECK constraint and every analytics aggregation. | Batch 024 |
| 2026-04-17 | Core has zero Sentry SDK deps | `SentryPrivacyFilter` (PRD §10b PII scrubber) lives in `ContractEngine.Core/Observability/` and operates on plain `IDictionary<string,string>` and `IDictionary<string,object?>` types — NOT `Sentry.SentryEvent.Request.Headers` or `SentryEvent.Extra` directly. Rationale: Core has zero external dependencies by architecture, so importing `Sentry.AspNetCore` into Core would break the dependency hierarchy. Program.cs (in Api, which CAN reference Sentry) adapts the SDK types into dictionaries before calling Scrub and writes the scrubbed values back. This also makes the scrubbing logic fully unit-testable without the Sentry SDK. When adding new PII scrubbing rules, edit Core; when adding new adapter call sites (e.g. scrubbing `sentryEvent.Contexts`), edit Program.cs. | Batch 025 |
| 2026-04-17 | Sentry SetBeforeSend takes a function, not a delegate | Sentry SDK 4.x exposes `SentryOptions.SetBeforeSend(Func<SentryEvent, SentryEvent?>)` — the older 3.x callers set `options.BeforeSend = ev => ...` as a property. In 4.x, property access is blocked and you MUST call `options.SetBeforeSend(...)`. If you see `'SentryOptions' does not contain a definition for 'BeforeSend'` on a 4.x upgrade, that's the cause. | Batch 025 |
| 2026-04-17 | Sentry + Serilog dual wire | ASP.NET Core middleware exceptions are captured by `UseSentry()` (WebHost level). Serilog events at Error / Fatal level are forwarded by a separate `.WriteTo.Sentry(o => o.MinimumEventLevel = LogEventLevel.Error)` sink configured inside the `UseSerilog` callback. Both sources share the DSN via the `sentryEnabled` bool computed once at startup. Without the Serilog sink, `_logger.LogError("Fire-and-forget publish failed")` calls in ecosystem wiring (`DeadlineAlertService`, `ObligationService.EmitTransitionSideEffectsAsync`) would never surface in Sentry because they don't throw. | Batch 025 |
| 2026-04-17 | --seed-hub-templates is create-only | The seeder treats 409 Conflict as idempotent success and continues — it CANNOT update a template body in place. To change a template body you MUST delete the template on Hub first (`DELETE /api/templates/{type}`), then re-run `--seed-hub-templates`. Do NOT change the seeder to PUT/upsert — the `409 → success` semantics are load-bearing for the "safe to re-run any time" contract that the runbook and operator muscle memory depend on. If amendment of existing templates becomes a real need, ship a separate `--update-hub-templates` command with explicit delete-then-recreate semantics. | Batch 025 |
| 2026-04-17 | CLI short-circuit order | Program.cs now has THREE CLI short-circuits: `--seed` (first-run tenant + holidays), `--seed-hub-templates` (Hub onboarding), and the main `app.Run()` path. The ordering matters — each short-circuit MUST return before `app.Build()` side effects (DB migration, scheduler wire-up, Kestrel bind) so an operator running `--seed-hub-templates` on a machine with no DB or no Postgres reachable still gets a clean exit code. Add new CLI flags immediately after existing short-circuits and BEFORE the AUTO_MIGRATE block. | Batch 025 |
| 2026-04-17 | Batch 026 reverted | GitHub Actions CI/CD + Hetzner VPS deployment (commit 1face1c) was reverted in commit eee50d9 per 2026-04-17 user direction. Out-of-scope artifacts that DO NOT exist on HEAD: `.github/workflows/ci.yml`, `.github/workflows/deploy.yml`, `scripts/deploy/*.sh`, `docs/operations/hetzner-deployment.md`, `docs/operations/ci-cd-pipeline.md`, YamlDotNet workflow-smoke-test dependency, `.gitattributes`, Caddy-hardened compose. `docker-compose.prod.yml` retains pre-Batch-026 shape (GHCR image + Caddy labels, no network segmentation). Do NOT document Batch-026 gotchas (GHCR lowercase, caddy-docker-proxy labels, Ubuntu 24.04 docker-compose syntax, etc.) — they are load-bearing only when CI/CD resumes. | Revert (eee50d9) |

## Shared Foundation (MUST READ before any implementation)

> These files define the project's shared patterns, configuration, and utilities.
> The AI MUST read these **in full** before writing ANY new code.
> Phase 1 is CLOSED — all rows below are `present`. Phase 2 / 3 work adds new rows.

| Category | File(s) | Status | Binding Specs |
|----------|---------|--------|---------------|
| DB context | `src/ContractEngine.Infrastructure/Data/ContractDbContext.cs` | present | CODEBASE_CONTEXT `Key Patterns §4` (EF Core global query filter via `ITenantScoped` marker + `ApplyTenantQueryFilter<T>`). Owns 10 DbSets: Tenants, Counterparties, Contracts, ContractDocuments, ContractTags, ContractVersions, Obligations, ObligationEvents, HolidayCalendars, DeadlineAlerts. |
| DI registration (Infrastructure) | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` | present | CODEBASE_CONTEXT `Key Patterns §3, §5`. Registers `ContractDbContext` (Npgsql), scoped `TenantContextAccessor` aliased to `ITenantContext`, all repositories + services, `LocalDocumentStorage` (singleton), `IMemoryCache`, `IBusinessDayCalculator` (singleton via factory pattern), `IAnalyticsQueryContext`, and FluentValidation assembly scan from Core. |
| DI registration (Jobs) | `src/ContractEngine.Jobs/ServiceRegistration.cs` | present | Registers `FirstRunSeeder`, `IDeadlineScanStore`, `IDeadlineAlertWriter`, `DeadlineScannerConfig` always. When `JOBS_ENABLED != false` (default `true`), also wires `AddQuartz` with `DeadlineScannerJob` + hourly cron trigger and `AddQuartzHostedService(WaitForJobsToComplete = true)`. |
| Tenant context | `src/ContractEngine.Core/Abstractions/ITenantContext.cs`, `NullTenantContext.cs`, `ITenantScoped.cs` | present | CODEBASE_CONTEXT `Key Patterns §3, §4`. `ITenantContext` backed at runtime by `TenantContextAccessor` (Infrastructure). `NullTenantContext` retained as a test-only fallback for code paths that never flow through middleware. |
| Tenant accessor | `src/ContractEngine.Infrastructure/Tenancy/TenantContextAccessor.cs` | present | Scoped concrete `ITenantContext` with `Resolve(Guid)` / `Clear()` write surface; `TenantResolutionMiddleware` holds the only writer reference (via DI resolution of the same instance). |
| Error handling | `src/ContractEngine.Api/Middleware/ExceptionHandlingMiddleware.cs` + `ErrorResponse.cs` | present | Maps `ValidationException`→400 VALIDATION_ERROR, `KeyNotFoundException`→404 NOT_FOUND, `UnauthorizedAccessException`→401 UNAUTHORIZED, `EntityTransitionException` (base for Contract + Obligation)→422 INVALID_TRANSITION (with valid next states in `details[]`), `InvalidOperationException`→409 CONFLICT, other→500. Ordering is load-bearing: specific types BEFORE the generic `InvalidOperationException` arm. Suppresses exception detail outside `Development`. |
| Tenant resolution | `src/ContractEngine.Api/Middleware/TenantResolutionMiddleware.cs` | present | Reads `X-API-Key` → SHA-256 → `ITenantRepository.GetByApiKeyHashAsync` → writes `TenantContextAccessor` when the row is active. Missing / malformed / unknown / inactive keys leave the context unresolved WITHOUT rejecting the request — public endpoints stay accessible; rejection is the endpoint's concern. |
| Request logging | `src/ContractEngine.Api/Middleware/RequestLoggingMiddleware.cs` | present | Pushes `request_id`/`tenant_id`/`module` via Serilog `LogContext.PushProperty` and emits a completion log with `StatusCode` and `ElapsedMs`. `module` derives from `/api/{module}` path or first path segment; defaults to `http`. |
| Tenant entity + service + repository | `src/ContractEngine.Core/Models/Tenant.cs`, `Services/TenantService.cs`, `Interfaces/ITenantRepository.cs` + `Infrastructure/Repositories/TenantRepository.cs` | present | PRD §4.1, §5.1. `RegisterAsync` mints a `cle_live_{32_hex}` key, SHA-256 hashes it, stores first 12 chars as `api_key_prefix`. `GetByApiKeyHashAsync` bypasses the tenant query filter via `IgnoreQueryFilters()`. |
| Tenant endpoints | `src/ContractEngine.Api/Endpoints/TenantEndpoints.cs` + `Endpoints/Dto/{RegisterTenantRequest,TenantMeResponse,PatchTenantMeRequest}.cs` | present | `POST /api/tenants/register` (public, `SELF_REGISTRATION_ENABLED` gated; 404 when disabled). `GET /api/tenants/me` (read-100). `PATCH /api/tenants/me` (write-20). Both `/me` endpoints guard on `ITenantContext.IsResolved` → 401. |
| Pagination primitives | `src/ContractEngine.Core/Pagination/PaginationCursor.cs`, `PageRequest.cs`, `PagedResult.cs`, `IHasCursor.cs` | present | `PaginationCursor` encodes/decodes opaque base64 `{created_at_iso}|{id_guid}` tokens; `PageRequest` clamps page size to [1, 100] with default 25; `PagedResult<T>` carries `{ data, pagination: { next_cursor, has_more, total_count } }`. |
| Cursor pagination extension | `src/ContractEngine.Infrastructure/Pagination/CursorPaginationExtensions.cs` | present | EF Core `IQueryable<T>.ApplyCursorAsync(PageRequest)` for any `T : IHasCursor`. Applies cursor WHERE clause, optional `created_after`/`created_before` filters, default desc `(CreatedAt, Id)` ordering, and fetches `PageSize + 1` rows to detect `HasMore` in one round-trip. |
| Rate limiter | `src/ContractEngine.Api/RateLimiting/RateLimitPolicies.cs`, `RateLimitConfiguration.cs` | present | Policies: `public` (5/min), `read-100` (100/min), `write-50` (50/min), `write-20` (20/min), `write-10` (10/min). Partitions on `X-API-Key` for authenticated calls, client IP for public. On 429 emits the canonical error envelope with `code = "RATE_LIMITED"`. Limits overridable via `RATE_LIMIT__*` config keys for tests. |
| Validators | `src/ContractEngine.Core/Validation/*Validator.cs` (12 files) | present | FluentValidation 11.x — `RegisterTenantRequestValidator`, `PatchTenantMeRequestValidator`, `CounterpartyValidators`, `ContractValidators`, `ContractTagVersionValidators`, `ObligationValidator`. Assembly-scanned in `ServiceRegistration.cs`. |
| Counterparty | `src/ContractEngine.Core/Models/Counterparty.cs`, `Services/CounterpartyService.cs`, `Interfaces/ICounterpartyRepository.cs` + `Infrastructure/Repositories/CounterpartyRepository.cs`, `Api/Endpoints/CounterpartyEndpoints.cs` | present | First `ITenantScoped` entity. CRUD + search (ILIKE on name). `GetContractCountAsync` runs tenant-filtered `CountAsync` against `contracts`. |
| Contract | `src/ContractEngine.Core/Models/Contract.cs`, `Enums/ContractStatus.cs`, `ContractType.cs`, `Services/ContractService.cs` (CRUD + compliance publisher wiring), `ContractService.Lifecycle.cs` (partial — `ActivateAsync`/`TerminateAsync`/`ArchiveAsync` lifecycle methods), `ContractRequests.cs` (request records: `CreateContractRequest`, `UpdateContractRequest`, etc.), `Interfaces/IContractRepository.cs` + `Infrastructure/Repositories/ContractRepository.cs`, `Api/Endpoints/ContractEndpoints.cs` | present | PRD §4.3 transition map enforced by `ActivateAsync`, `TerminateAsync`, `ArchiveAsync` (all on the `Lifecycle` partial). `ArchiveAsync` delegates to `ObligationService.ExpireDueToContractArchiveAsync` (obligation cascade) for all non-terminal obligations. Invalid transitions throw `ContractTransitionException` → 422 INVALID_TRANSITION. JSON enum policy `JsonStringEnumConverter(SnakeCaseLower)` in `Program.cs`. Post-Batch-025 modularity split: pre-split `ContractService.cs` was 486 lines — now 273 + 207 + 49 across three files, all under 300-line cap. |
| Entity transition exceptions | `src/ContractEngine.Core/Exceptions/EntityTransitionException.cs`, `ContractTransitionException.cs`, `ObligationTransitionException.cs` | present | Abstract base class + two concrete subclasses. `IReadOnlyList<string> ValidNextStates` (snake_case lowercase) keeps the middleware enum-agnostic; shadowed typed properties give in-process callers the original enum type. Middleware matches the base type BEFORE the generic `InvalidOperationException` → 409 arm (ordering is load-bearing). |
| Contract documents | `src/ContractEngine.Core/Models/ContractDocument.cs`, `Services/ContractDocumentService.cs`, `Interfaces/IContractDocumentRepository.cs`, `IDocumentStorage.cs`, `Infrastructure/Storage/LocalDocumentStorage.cs`, `Infrastructure/Repositories/ContractDocumentRepository.cs`, `Api/Endpoints/ContractDocumentEndpoints.cs` | present | Multipart upload (manual form parse to bypass `[FromForm]` 500 bug). Storage layout `{root}/{tenant_id}/{contract_id}/{filename}` via `LocalDocumentStorage` (singleton, root from `DOCUMENT_STORAGE_PATH`). Upload to archived contract → 409 CONFLICT. `.DisableAntiforgery()` on upload route. |
| Contract tags | `src/ContractEngine.Core/Models/ContractTag.cs`, `Services/ContractTagService.cs`, `Interfaces/IContractTagRepository.cs` + `Infrastructure/Repositories/ContractTagRepository.cs`, `Api/Endpoints/ContractTagEndpoints.cs` | present | REPLACE semantics: `POST /api/contracts/{id}/tags` clears and re-inserts inside a transaction. UNIQUE `(tenant_id, contract_id, tag)` at DB level. Case-sensitive per PRD §4.12. |
| Contract versions | `src/ContractEngine.Core/Models/ContractVersion.cs`, `Services/ContractVersionService.cs`, `Interfaces/IContractVersionRepository.cs` + `Infrastructure/Repositories/ContractVersionRepository.cs`, `Api/Endpoints/ContractVersionEndpoints.cs` | present | `CreateAsync` computes `MAX(version_number)+1` (clamped to be > `Contract.CurrentVersion`), persists, then bumps `Contract.CurrentVersion` via a separate `UpdateAsync` (two SaveChanges — documented trade-off). List newest-first via cursor helper. |
| Obligations | `src/ContractEngine.Core/Models/Obligation.cs`, `ObligationEvent.cs`, `Enums/{ObligationStatus,ObligationType,ObligationRecurrence,ObligationSource,ResponsibleParty,DisputeResolution}.cs`, `Services/ObligationStateMachine.cs`, `ObligationService.cs`, `Interfaces/{IObligationRepository,IObligationEventRepository}.cs` + `Infrastructure/Repositories/*.cs`, `Api/Endpoints/ObligationEndpoints.cs` | present | **Full 10-endpoint surface (Batches 011–013).** `ObligationStateMachine` is a stateless singleton (`GetValidNextStates`, `EnsureTransitionAllowed`, `IsTerminal`). Terminal: Dismissed, Fulfilled, Waived, Expired. `ObligationService.FulfillAsync` spawns a new Active obligation with `next_due_date` advanced by recurrence (monthly/quarterly/annually). `ExpireDueToContractArchiveAsync(contractId, actor)` is the archive cascade entry point. Events are INSERT-ONLY at the interface level (reflection test enforces it). |
| HolidayCalendar | `src/ContractEngine.Core/Models/HolidayCalendar.cs`, `Interfaces/IHolidayCalendarRepository.cs`, `IHolidayCalendarRepositoryFactory.cs`, `Infrastructure/Repositories/HolidayCalendarRepository.cs`, `HolidayCalendarRepositoryFactory.cs`, `Data/HolidayCalendarSeeder.cs` | present | Seeder hardcodes US/DE/UK/NL holidays for 2026 and 2027 (~80 rows). Repository merges system-wide + tenant-specific rows; tenant-specific wins on duplicate date. Factory provides per-call scoped DbContext for the singleton `BusinessDayCalculator`. |
| BusinessDayCalculator | `src/ContractEngine.Core/Interfaces/IBusinessDayCalculator.cs` + `Services/BusinessDayCalculator.cs` | present | DI singleton. 24 h `IMemoryCache` keyed on `"holidays::{code}::{year}::{tenantId?}"`. Three methods: `BusinessDaysUntil`, `BusinessDaysAfter`, `IsBusinessDay`. Static `ForTesting` factory bypasses the scope factory. |
| Deadline alerts | `src/ContractEngine.Core/Models/DeadlineAlert.cs`, `Enums/AlertType.cs`, `Services/DeadlineAlertService.cs`, `AlertFilters.cs`, `Interfaces/IDeadlineAlertRepository.cs` + `Infrastructure/Repositories/DeadlineAlertRepository.cs`, `Api/Endpoints/AlertEndpoints.cs` | present | Service-level idempotency on `(obligation_id, alert_type, days_remaining)`. Bulk ack uses EF Core 8 `ExecuteUpdateAsync` (single round-trip). No public CREATE endpoint — alerts come from `DeadlineScannerJob`. |
| Deadline scanner | `src/ContractEngine.Core/Services/DeadlineScannerCore.cs`, `Interfaces/{IDeadlineScanStore,IDeadlineAlertWriter}.cs`, `Infrastructure/Jobs/DeadlineScanStore.cs`, `DeadlineAlertWriter.cs`, `Jobs/DeadlineScannerJob.cs` | present | Hourly Quartz job (`0 0 * * * ?`, `[DisallowConcurrentExecution]`). `DeadlineScannerCore` iterates non-terminal obligations, computes business days, applies transition matrix (active→upcoming→due→overdue→escalated), writes `obligation_events`, creates alerts. `DeadlineAlertWriter` bridges the tenantless scanner to the tenant-scoped `DeadlineAlertService` via per-call child DI scope. |
| First-run seeder | `src/ContractEngine.Infrastructure/Data/FirstRunSeeder.cs` | present | PRD §11. Idempotent: skip if any tenant exists. Invoked from `Program.cs` on `--seed` CLI AND on `AUTO_SEED=true` first boot. Calls `TenantService.RegisterAsync` + `HolidayCalendarSeeder.SeedAsync`, returns plaintext API key once. |
| Analytics | `src/ContractEngine.Core/Services/AnalyticsService.cs`, `Interfaces/IAnalyticsQueryContext.cs` + `Infrastructure/Analytics/EfAnalyticsQueryContext.cs`, `Api/Endpoints/AnalyticsEndpoints.cs` | present | Dashboard + 3 aggregations at `write-50` rate limit (queries are read-only but hit multiple tables; PRD explicitly caps them). Decimals serialise as canonical `"N.NN"` strings. `deadline-calendar` hard-caps at 365 days + 1000 rows. |
| Health endpoints | `src/ContractEngine.Api/Endpoints/HealthEndpoints.cs` + `Endpoints/Dto/HealthResponses.cs` | present | `/health`, `/health/db` (runs `SELECT 1`), `/health/ready` (aggregates DB + 6 integration flags). All public, no rate limit. |
| App entry point | `src/ContractEngine.Api/Program.cs` | present | Minimal API host. Serilog bootstrap with test-supplied logger detection. `--seed` CLI short-circuit. `AUTO_MIGRATE` (default true) runs `Database.MigrateAsync()` before pipeline starts; `Testing` environment short-circuits. `AUTO_SEED` (default true) populates holiday calendars + first-run tenant. Middleware order: ExceptionHandling → RequestLogging → TenantResolution → RateLimiter → routes. Registers 12 endpoint groups. JSON enum policy = `SnakeCaseLower`. |
| IRagPlatformClient | `src/ContractEngine.Core/Interfaces/IRagPlatformClient.cs` | present | **Batch 019.** PRD §5.6a. Four methods: `UploadDocumentAsync`, `SearchAsync`, `ChatSyncAsync`, `GetEntitiesAsync`. Return-shape policy for the no-op is part of the interface contract: reads return empty, writes throw — writes must fail loudly so extraction pipelines don't silently skip work. |
| RAG DTOs | `src/ContractEngine.Core/Integrations/Rag/RagDocument.cs`, `RagSearchResult.cs`, `RagChatResult.cs`, `RagEntity.cs`, `RagPlatformException.cs` | present | **Batch 019.** Pure records mirroring the RAG Platform JSON contract. `RagPlatformException` carries upstream status code + best-effort response body; raised by the real client on non-success HTTP and by the resilience pipeline after retries exhaust. |
| RagPlatformClient | `src/ContractEngine.Infrastructure/External/RagPlatformClient.cs` | present | **Batch 019.** Typed `HttpClient` constructed via `IHttpClientFactory`. Per-request `X-API-Key` header from `RAG_PLATFORM_API_KEY`. Snake-case JSON (`JsonNamingPolicy.SnakeCaseLower`) on both wire sides. Private wire DTOs map into the Core records so the public contract stays decoupled from upstream schema drift. Non-success statuses → `RagPlatformException` with code + body. |
| NoOpRagPlatformClient | `src/ContractEngine.Infrastructure/Stubs/NoOpRagPlatformClient.cs` | present | **Batch 019.** Registered when `RAG_PLATFORM_ENABLED=false` (the default). `SearchAsync` / `GetEntitiesAsync` return empty; `UploadDocumentAsync` / `ChatSyncAsync` throw `InvalidOperationException("RAG Platform is disabled (RAG_PLATFORM_ENABLED=false)")`. The throw/return split is deliberate — see interface docs. |
| RAG DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` (`AddRagPlatformClient`) | present | **Batch 019.** Reads `RAG_PLATFORM_ENABLED` (default false). ENABLED=true branch: validates `RAG_PLATFORM_URL` (throws at DI build if missing), adds typed `HttpClient` with 30s timeout + `AddResilienceHandler("rag-platform")` pipeline: retry 3× exponential 1s → 3s → 9s + circuit breaker opening on 5 consecutive failures, 30s break. ENABLED=false branch: registers the NoOp singleton. |
| ExtractionPrompt entity | `src/ContractEngine.Core/Models/ExtractionPrompt.cs` | present | **Batch 020.** PRD §4.11. NOT `ITenantScoped` (nullable tenant_id — system-default rows must be visible). UNIQUE `(tenant_id, prompt_type)` NULLS NOT DISTINCT. Repo handles isolation with explicit `WHERE tenant_id = @id OR tenant_id IS NULL`, prioritising tenant-specific over system-default. |
| ExtractionJob entity | `src/ContractEngine.Core/Models/ExtractionJob.cs`, `Enums/ExtractionStatus.cs` | present | **Batch 020.** PRD §4.8. `ITenantScoped` + `IHasCursor`. Five-state lifecycle: Queued → Processing → Completed/Partial/Failed. `PromptTypes` stored as `TEXT[]` (Npgsql native), `RawResponses` as JSONB. |
| ExtractionDefaults | `src/ContractEngine.Core/Defaults/ExtractionDefaults.cs` | present | **Batch 020.** Hardcoded extraction prompts for payment, renewal, compliance, performance. `AllPromptTypes` returns the canonical type list; `GetByType(promptType)` resolves default prompt text. |
| ExtractionPromptRepository | `src/ContractEngine.Core/Interfaces/IExtractionPromptRepository.cs` + `Infrastructure/Repositories/ExtractionPromptRepository.cs` | present | **Batch 020.** `GetPromptAsync(tenantId, promptType)` resolves tenant-specific → system-default fallback. `ListByTenantAsync` returns active prompts. No global query filter (entity is not `ITenantScoped`). |
| ExtractionJobRepository | `src/ContractEngine.Core/Interfaces/IExtractionJobRepository.cs` + `Infrastructure/Repositories/ExtractionJobRepository.cs` | present | **Batch 020.** `ListAsync(ExtractionJobFilters, PageRequest)` tenant-scoped via global query filter. `ListQueuedAsync(batchSize)` uses `IgnoreQueryFilters()` for the cross-tenant background processor. |
| ExtractionService | `src/ContractEngine.Core/Services/ExtractionService.cs` (orchestration surface — `TriggerExtractionAsync` / `RetryExtractionAsync` / `ExecuteExtractionAsync`), `ExtractionService.Pipeline.cs` (partial — `UploadDocumentToRagAsync` + per-prompt chat loop + raw-response persistence), `ExtractionResultParser.cs` (JSON → `Obligation` row mapping with confidence/clause extraction) | present | **Batch 021 (refactored post-Batch-025).** PRD §5.2. `TriggerExtractionAsync` validates contract + optional document, creates Queued job. `ExecuteExtractionAsync` (called by ExtractionProcessorJob) uploads to RAG (Pipeline partial), runs prompts, parses obligations (Pending status) via `ExtractionResultParser`, stores raw_responses. `RetryExtractionAsync` resets Failed/Partial → Queued. Post-Batch-025 modularity split: pre-split `ExtractionService.cs` was 424 lines — now 204 + 151 + 110, all under 300-line cap. |
| ExtractionProcessorJob | `src/ContractEngine.Jobs/ExtractionProcessorJob.cs` | present | **Batch 021.** Every 5 min Quartz job. `ListQueuedAsync(batchSize)` cross-tenant, resolves each job's tenant via `TenantContextAccessor.Resolve(tenantId)` in a child DI scope, delegates to `ExtractionService.ExecuteExtractionAsync`. |
| ExtractionEndpoints | `src/ContractEngine.Api/Endpoints/ExtractionEndpoints.cs` + DTOs | present | **Batch 021.** PRD §8b. `POST /api/contracts/{id}/extract` (write-10), `GET /api/extraction-jobs` (read-100), `GET /api/extraction-jobs/{id}` (read-100), `POST /api/extraction-jobs/{id}/retry` (write-10). All require resolved tenant. Detail endpoint exposes `raw_responses` (JSONB). |
| ContractDiffService | `src/ContractEngine.Core/Services/ContractDiffService.cs` | present | **Batch 022.** PRD §5.5. Loads two versions, validates RAG docs exist, calls `ChatSyncAsync` with diff prompt, parses into JSONB, stores on newer version's `diff_result`. Returns `VersionDiffResult` envelope. |
| ConflictDetectionService | `src/ContractEngine.Core/Services/ConflictDetectionService.cs` | present | **Batch 022.** PRD §5.5. On contract activation, queries up to 5 other active contracts with same counterparty, asks RAG to identify conflicting clauses. Returns `ConflictInfo` list. Silently returns empty when RAG is disabled. |
| ContractVersionEndpoints (diff) | `src/ContractEngine.Api/Endpoints/ContractVersionEndpoints.cs` (DiffAsync) | present | **Batch 022.** `GET /api/contracts/{id}/versions/{v}/diff?compare_to=` (write-20). Validates contract exists, calls `ContractDiffService.DiffVersionsAsync`. Missing RAG docs → 409 CONFLICT via `InvalidOperationException`. |
| AutoRenewalMonitorCore + Job | `src/ContractEngine.Core/Services/AutoRenewalMonitorCore.cs`, `Interfaces/IAutoRenewalStore.cs`, `Infrastructure/Jobs/AutoRenewalStore.cs`, `Jobs/AutoRenewalMonitorJob.cs` | present | **Batch 022.** PRD §7. Daily 6 AM UTC scan. Finds Expiring + auto_renewal=true contracts, transitions to Active with extended end_date, creates version + alert. |
| StaleObligationCheckerCore + Job | `src/ContractEngine.Core/Services/StaleObligationCheckerCore.cs`, `Interfaces/IStaleObligationStore.cs`, `Infrastructure/Jobs/StaleObligationStore.cs`, `Jobs/StaleObligationCheckerJob.cs` | present | **Batch 022.** PRD §7. Weekly Monday 9 AM UTC data integrity sweep. Logs warnings for stale non-terminal obligations with past `next_due_date`. Conservative: no auto-transition. |
| INotificationPublisher | `src/ContractEngine.Core/Interfaces/INotificationPublisher.cs` + `Integrations/Notifications/NotificationDispatchResult.cs` | present | **Batch 023.** PRD §5.6b. One method: `PublishEventAsync(eventType, payload, ct) → NotificationDispatchResult(Dispatched)`. Fire-and-forget semantics — the no-op returns `Dispatched=false` and MUST NOT throw; real client catches upstream errors and logs + returns not-dispatched. Missing notifications NEVER roll back the domain transaction. |
| NotificationHubClient | `src/ContractEngine.Infrastructure/External/NotificationHubClient.cs` | present | **Batch 023.** Typed `HttpClient` via `IHttpClientFactory`. `POST /api/events` with `X-API-Key` from `NOTIFICATION_HUB_API_KEY`. Snake-case JSON envelope `{ event_type, payload, source: "contract-engine" }`. Non-2xx → log warning + return not-dispatched (never throw — Hub failures are recoverable). |
| NoOpNotificationPublisher | `src/ContractEngine.Infrastructure/Stubs/NoOpNotificationPublisher.cs` | present | **Batch 023.** Registered when `NOTIFICATION_HUB_ENABLED=false` (the default). Returns `Dispatched=false` with no side effects. Tests assert: never throws, returns the canonical not-dispatched result. |
| Notification Hub DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` (`AddNotificationHub`) | present | **Batch 023.** Reads `NOTIFICATION_HUB_ENABLED`. ENABLED=true: typed `HttpClient` with 30s timeout + shared `ConfigureEcosystemResilience("notification-hub")` pipeline (3× exponential retry + 5-failure/30s circuit breaker). ENABLED=false: NoOp singleton. |
| IWorkflowTrigger | `src/ContractEngine.Core/Interfaces/IWorkflowTrigger.cs` + `Integrations/Workflow/WorkflowTriggerResult.cs`, `WorkflowEngineException.cs` | present | **Batch 023.** PRD §5.6d. `TriggerWorkflowAsync(webhookPath, payload, ct) → WorkflowTriggerResult(Triggered, InstanceId?)`. No call-site wiring in Batch 023 — the interface lands ahead of the workflow design so amendment-approval flows can be added later without DI churn. |
| WorkflowEngineClient | `src/ContractEngine.Infrastructure/External/WorkflowEngineClient.cs` | present | **Batch 023.** Typed `HttpClient`. `POST /webhooks/{webhookPath}` with `X-API-Key` from `WORKFLOW_ENGINE_API_KEY`. Parses optional `instance_id` echo from the response body. Non-success → `WorkflowEngineException`. |
| NoOpWorkflowTrigger | `src/ContractEngine.Infrastructure/Stubs/NoOpWorkflowTrigger.cs` | present | **Batch 023.** Registered when `WORKFLOW_ENGINE_ENABLED=false`. Returns `WorkflowTriggerResult(Triggered: false)`. Never throws. |
| IComplianceEventPublisher | `src/ContractEngine.Core/Interfaces/IComplianceEventPublisher.cs` + `Integrations/Compliance/ComplianceEventEnvelope.cs`, `ComplianceLedgerException.cs` | present | **Batch 023.** PRD §5.6e. `PublishAsync(subject, envelope, ct) → Task<bool>`. Envelope is a record: `{ EventType, TenantId, Timestamp, Payload, Source = "contract-engine" }`. Subjects: `contract.obligation.breached`, `contract.renewed`, `contract.terminated`. |
| ComplianceLedgerNatsPublisher | `src/ContractEngine.Infrastructure/External/ComplianceLedgerNatsPublisher.cs` | present | **Batch 023.** NATS.Client 1.1.8 singleton. Lazy connect via `EnsureConnection()` with `lock` guard + `{ State: ConnState.CONNECTED }` pattern match (the v1 API can't use `IsClosed` in patterns — it's set-only). `Publish(subject, bytes)` + `Flush(5000)` int-ms (NOT TimeSpan — v1 API takes milliseconds). `IDisposable` calls `Drain()` on shutdown for graceful disconnect. Fire-and-forget — catches all exceptions + returns `false`. |
| NoOpCompliancePublisher | `src/ContractEngine.Infrastructure/Stubs/NoOpCompliancePublisher.cs` | present | **Batch 023.** Registered when `COMPLIANCE_LEDGER_ENABLED=false`. Returns `false`. Never throws. |
| Compliance Ledger DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` (`AddComplianceLedger`) | present | **Batch 023.** Reads `COMPLIANCE_LEDGER_ENABLED`. ENABLED=true: registers `ComplianceLedgerNatsPublisher` as **singleton** (connection reuse + shutdown lifecycle) with `NATS_URL` validation. ENABLED=false: NoOp singleton. |
| IInvoiceReconClient | `src/ContractEngine.Core/Interfaces/IInvoiceReconClient.cs` + `Integrations/InvoiceRecon/PurchaseOrderRequest.cs`, `PurchaseOrderResult.cs`, `InvoiceReconException.cs` | present | **Batch 023.** PRD §5.6f. `CreatePurchaseOrderAsync(tenantApiKey, PurchaseOrderRequest, ct) → PurchaseOrderResult`. Called from `ObligationService.ConfirmAsync` when `ObligationType == Payment`. |
| InvoiceReconClient | `src/ContractEngine.Infrastructure/External/InvoiceReconClient.cs` | present | **Batch 023.** Typed `HttpClient`. `POST /api/purchase-orders` with TWO headers: `X-API-Key` (system) from `INVOICE_RECON_API_KEY` + `X-Tenant-API-Key` (per-call) — Invoice Recon needs both to route PO to the right tenant. Non-success → `InvoiceReconException`. |
| NoOpInvoiceReconClient | `src/ContractEngine.Infrastructure/Stubs/NoOpInvoiceReconClient.cs` | present | **Batch 023.** Registered when `INVOICE_RECON_ENABLED=false`. Returns `PurchaseOrderResult(Created: false, PurchaseOrderId: null)`. Never throws. |
| Notification Hub event wiring | `src/ContractEngine.Core/Services/DeadlineAlertService.cs`, `ObligationService.cs` (`EmitTransitionSideEffectsAsync`) | present | **Batch 023.** `DeadlineAlertService.CreateIfNotExistsAsync` emits Hub event AFTER alert persistence; on successful dispatch, stamps `notification_sent=true`. `ObligationService.TransitionAsync` calls `EmitTransitionSideEffectsAsync(existing, fromStatus, targetStatus, ...)` AFTER the event row commits. Emissions are fire-and-forget — publisher failures caught + logged (WARN), domain transaction never rolls back. AlertType→event map: `deadline_approaching`→`obligation.deadline.approaching`, `obligation_overdue`→`obligation.overdue`, `contract_expiring`→`contract.expiring`, `auto_renewal_warning`→`contract.auto_renewed`, `contract_conflict`→`contract.conflict_detected`. |
| Compliance Ledger event wiring | `src/ContractEngine.Core/Services/ObligationService.cs`, `AutoRenewalMonitorCore.cs`, `ContractService.cs` | present | **Batch 023.** ObligationService publishes `contract.obligation.breached` when transitioning to Overdue/Escalated. AutoRenewalMonitorCore publishes `contract.renewed` after each successful renewal commit. ContractService publishes `contract.terminated` after `TerminateAsync` commits. All emissions are fire-and-forget with try/catch + log, never roll back. |
| Invoice Recon wiring | `src/ContractEngine.Core/Services/ObligationService.Transitions.cs` (`ConfirmAsync` → `EmitInvoiceReconAsync`) | present | **Batch 023.** On `pending → active` transition for `ObligationType.Payment`, emits a PO to Invoice Recon after the DB commit. Looks up tenant's API key via `ITenantRepository` and forwards as `X-Tenant-API-Key`. Failures caught + logged — confirmation never rolls back over a missed PO. |
| Optional-ctor legacy test pattern | `src/ContractEngine.Core/Services/DeadlineAlertService.cs`, `ObligationService.cs`, `AutoRenewalMonitorCore.cs`, `ContractService.cs` | present | **Batch 023.** New ecosystem dependencies registered as OPTIONAL ctor params (default = null) with private `NullXxxPublisher` / `NullXxxClient` fallbacks behind the null-coalescing. Keeps legacy test constructors (`new Service(repo, tenantContext, stateMachine)`) compiling without forcing every test to learn the new DI surface. Production DI always resolves a real implementation (either the live client or the no-op stub), so the null fallbacks never fire in production. |
| SignedContractPayload | `src/ContractEngine.Core/Integrations/Webhooks/SignedContractPayload.cs` | present | **Batch 024.** Immutable record `{ Source, ExternalId, Title, CounterpartyName, DownloadUrl, FileName, CompletedAt? }` — the normalised shape both DocuSign and PandaDoc payloads collapse into. `ExternalId` becomes either `webhook_envelope_id` or `webhook_document_id` in the draft contract's JSONB metadata. |
| WebhookPayloadParser | `src/ContractEngine.Core/Integrations/Webhooks/WebhookPayloadParser.cs` | present | **Batch 024.** Pure-function singleton parser (zero dependencies). `Parse(source, body)` switches on `"docusign"` / `"pandadoc"`, returns null for unsupported sources, wrong event names (`envelope.completed` for DocuSign, anything containing `state_changed` with `data.status == document.completed` for PandaDoc), missing required fields, or malformed JSON. Counterparty resolution: DocuSign `signers[0].company` → `envelope_name` → `envelope_id`; PandaDoc `data.metadata.counterparty_name` → `data.name` → `data.id`. |
| IWebhookDocumentDownloader + WebhookDownloadException | `src/ContractEngine.Core/Interfaces/IWebhookDocumentDownloader.cs`, `src/ContractEngine.Core/Integrations/Webhooks/WebhookDownloadException.cs` | present | **Batch 024.** Single-method interface `Task<Stream> DownloadAsync(url, ct)`. Exception carries upstream `StatusCode` so the webhook handler can log status without exposing the signed URL in log output. |
| WebhookDocumentDownloader | `src/ContractEngine.Infrastructure/External/WebhookDocumentDownloader.cs` | present | **Batch 024.** Typed `HttpClient` via `IHttpClientFactory` (60 s timeout + shared `ConfigureEcosystemResilience("webhook-downloader")` retry/CB pipeline). Uses `HttpCompletionOption.ResponseHeadersRead` for streaming — body is consumed lazily by the storage-layer reader. Throws `ArgumentException` on empty URL, `WebhookDownloadException(statusCode, ...)` on non-success. NEVER logs the URL (signed credentials). |
| Webhook DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` (`AddWebhookEngine`) | present | **Batch 024.** Reads `WEBHOOK_ENGINE_ENABLED`. Always registers `WebhookPayloadParser` as a singleton (stateless, no config). ENABLED=true: typed `HttpClient` for `IWebhookDocumentDownloader` with 60 s timeout + shared ecosystem resilience pipeline. ENABLED=false: registers the downloader as a no-op that throws if called — safe because the endpoint short-circuits on 404 before any downloader call. |
| WebhookEndpoints | `src/ContractEngine.Api/Endpoints/WebhookEndpoints.cs` (route registration + HMAC signature gate only — 95 lines), `WebhookEndpointHelpers.cs` (phase helpers: `ResolveTenantAsync`, `FindExistingContractAsync`, `CreateDraftContractAsync`, `DownloadAndStoreDocumentAsync`, `TriggerExtractionAsync` — 253 lines) | present | **Batch 024 (refactored post-Batch-025 — b70fdf0).** `POST /api/webhooks/contract-signed?source={docusign\|pandadoc}` under the `PublicWebhook` rate-limit policy. Handler order: feature-gate 404 → raw body buffer → HMAC-SHA256 verify via `FixedTimeEquals` → `X-Tenant-Id` lookup via `ITenantRepository.GetByIdAsync` (IgnoreQueryFilters) → `TenantContextAccessor.Resolve(tenantId)` → parse payload (null → 202 ignored) → JSONB idempotency probe → Draft contract create with metadata → fire-and-forget download + upload + extraction trigger → 202 with `{ status, contract_id, idempotent }`. Draft contract defaults to `ContractType.Vendor` (payload doesn't carry type info). All 4xx guards return the canonical 401 envelope for signature/tenant failures and 404 for feature-disabled. Pre-split endpoint file was 356 lines → now 95 + 253 across two files, both under 300-line cap. |
| PublicWebhook rate-limit policy | `src/ContractEngine.Api/RateLimiting/RateLimitPolicies.cs`, `RateLimitConfiguration.cs` | present | **Batch 024.** New policy `"public-webhook"` with default **100/min** (vs the stricter `Public` policy's 5/min). Partitioned by client IP. Rationale: signed-contract completion bursts (MSA + multiple SOWs signed together) exceed 5/min for legitimate traffic. HMAC gates abuse, not rate limiting. Override via `RATE_LIMIT__PUBLIC_WEBHOOK`. |
| Webhook operator runbook | `docs/operations/webhook-engine-setup.md` | present | **Batch 024.** Operator-facing runbook: env-var setup (`WEBHOOK_ENGINE_ENABLED`, `WEBHOOK_SIGNING_SECRET`), Webhook Engine destination registration (URL, signature header format, `X-Tenant-Id` header), accepted DocuSign `envelope.completed` + PandaDoc `document_state_changed` payload shapes, response envelopes (202/401/404), idempotency guarantees, HMAC troubleshooting playbook, secret-rotation procedure, curl-based end-to-end test recipe. |
| SentryPrivacyFilter | `src/ContractEngine.Core/Observability/SentryPrivacyFilter.cs` | present | **Batch 025.** Pure static class with zero Sentry SDK deps. Operates on plain `IDictionary<string,string>` (headers) and `IDictionary<string,object?>` (extras). Blocklist: X-API-Key, X-Tenant-API-Key, X-Webhook-Signature, Authorization, Cookie, Set-Cookie, Proxy-Authorization (case-insensitive exact match). Fragment list: api_key, apikey, signature, password, secret, token (case-insensitive substring). Replaces sensitive values with `SentryPrivacyFilter.RedactedMarker` = `"[REDACTED]"`. Recurses into nested `IDictionary<string,object?>` payloads so `{ "nested": { "token": "..." } }` is also scrubbed. |
| Sentry wiring | `src/ContractEngine.Api/Program.cs` (UseSentry block + UseSerilog sink block) | present | **Batch 025.** Gated on non-empty `SENTRY_DSN` — empty = silently disabled. `builder.WebHost.UseSentry(options => ...)` sets TracesSampleRate=0.1, Environment from `builder.Environment.EnvironmentName`, Release from `typeof(Program).Assembly.GetName().Version`, SendDefaultPii=false. `BeforeSend` copies `sentryEvent.Request.Headers` into a Dictionary, calls `SentryPrivacyFilter.Scrub`, writes back. Separately, inside the `UseSerilog` callback, `.WriteTo.Sentry(o => o.MinimumEventLevel = LogEventLevel.Error)` forwards Serilog Error/Fatal events to Sentry so non-exception error-level logs also surface. |
| NotificationHubTemplateSeeder | `src/ContractEngine.Infrastructure/Data/NotificationHubTemplateSeeder.cs` | present | **Batch 025.** One-shot CLI helper. Ctor: `(HttpClient, ILogger<T>, IConfiguration)`. `SeedAsync()` POSTs 8 canonical templates (deadline_approaching, deadline_imminent, overdue, escalated, contract_expiring, auto_renewed, contract_conflict, extraction_complete) to `{NOTIFICATION_HUB_URL}/api/templates` with `X-API-Key` from `NOTIFICATION_HUB_API_KEY`. 409 Conflict treated as idempotent success. Continues on failure (aggregates counts) — exit code 0 on full success, 1 on any non-409 failure or empty URL. NOT wired to AUTO_SEED (operator-driven, one-shot). |
| --seed-hub-templates CLI handler | `src/ContractEngine.Api/Program.cs` (after `--seed` handler, before AUTO_MIGRATE) | present | **Batch 025.** Short-circuits before the web host starts. Builds a short-lived `HttpClient` + `LoggerFactory` adapter over the static `Log.Logger`, instantiates the seeder with `builder.Configuration`, awaits `SeedAsync()`, sets `Environment.ExitCode`, returns. No DI scope, no DB, no scheduler — safest possible CLI surface. |
| BetterStack operator runbook | `docs/operations/betterstack-setup.md` | present | **Batch 025.** Operator-facing runbook: monitors for /health (P1, 30s), /health/db (P1, 60s), /health/ready (P2, 60s) — each with expected status + body-contains assertions; alert policy (2-failure / 30s for P1, 3-failure for P2); team channel config; public status-page template; incident runbook excerpt covering paging flow, Sentry correlation, Docker compose rollback. |
| Notification Hub operator runbook | `docs/operations/notification-hub-setup.md` | present | **Batch 025.** Operator-facing runbook: `NOTIFICATION_HUB_URL` + `NOTIFICATION_HUB_API_KEY` env setup, CLI invocation via `dotnet run -- --seed-hub-templates` (and the docker-compose variant), expected output for happy / re-run / partial-failure cases, template-catalogue with placeholder variables, modification procedure (delete + re-seed because the seeder is create-only), rollback, `NOTIFICATION_HUB_ENABLED=false` kill switch. |
| Local services | `docker-compose.yml` + `docker-compose.override.yml` | present | PostgreSQL 16 on host 5445, NATS 2 on host 4225 (profile `nats`) |
| Production deployment | `docker-compose.prod.yml` + `Dockerfile` + `.dockerignore` | present | GHCR image `ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest`, `documents` named volume at `/app/data/documents`. Multi-stage Dockerfile (`sdk:8.0` build → `aspnet:8.0` runtime, 334 MB). `.dockerignore` excludes `tests/` + `bin/` + `obj/`. Retains pre-Batch-026 shape (GHCR + Caddy labels) — Caddy hardening + network segmentation NOT applied (Batch 026 reverted). Manual deploy only; no automated CI/CD pipeline. |
| Environment catalogue | `.env.example` | present | Complete env var list (committed); `.env` = local values (git-ignored). Batch 018 added `AUTO_MIGRATE=true`. |
| E2E server fixture | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` | present | Real Kestrel subprocess pattern: spawn compiled DLL → drain stdout/stderr → wait for TCP bind → assert HTTP. All 11 other E2E test classes follow this shape across ports 5050–5062 (Batch 024 added `WebhookEndpointE2ETests` on 5062). |
| API middleware test infrastructure | `tests/ContractEngine.Api.Tests/Middleware/InMemoryLogSink.cs`, `SerilogTestBootstrap.cs`, `WebApplicationCollection.cs` | present | Process-wide `ILogEventSink` with `Clear()` / `Snapshot()`. Idempotent Serilog bootstrap. `[CollectionDefinition("WebApplication", DisableParallelization = true)]` for xUnit serialisation of factory-using test classes. |
| Integration test fixture | `tests/ContractEngine.Integration.Tests/Fixtures/DatabaseFixture.cs` | present | Scoped to `contract_engine_test` DB. `InitializeAsync` runs `Database.MigrateAsync` + `HolidayCalendarSeeder.SeedAsync` on first instantiation; per-test cleanup via repository calls. |

## Deep References

| Topic | Where to look |
|-------|--------------|
| Contract lifecycle | `src/ContractEngine.Core/Services/ContractService.cs` + `ContractService.Lifecycle.cs` (partial) + `ContractRequests.cs` (request records) |
| Obligation state machine | `src/ContractEngine.Core/Services/ObligationStateMachine.cs` |
| Obligation service (CRUD + transitions + archive cascade) | `src/ContractEngine.Core/Services/ObligationService.cs` |
| Business day math | `src/ContractEngine.Core/Services/BusinessDayCalculator.cs` |
| Deadline alerts | `src/ContractEngine.Core/Services/DeadlineAlertService.cs` |
| Deadline scanner | `src/ContractEngine.Core/Services/DeadlineScannerCore.cs` + `src/ContractEngine.Jobs/DeadlineScannerJob.cs` |
| Analytics | `src/ContractEngine.Core/Services/AnalyticsService.cs` + `src/ContractEngine.Infrastructure/Analytics/EfAnalyticsQueryContext.cs` |
| Tenant resolution pipeline | `src/ContractEngine.Api/Middleware/TenantResolutionMiddleware.cs` + `src/ContractEngine.Infrastructure/Tenancy/TenantContextAccessor.cs` |
| Error envelope | `src/ContractEngine.Api/Middleware/ExceptionHandlingMiddleware.cs` |
| Request logging | `src/ContractEngine.Api/Middleware/RequestLoggingMiddleware.cs` |
| Cursor pagination | `src/ContractEngine.Infrastructure/Pagination/CursorPaginationExtensions.cs` |
| RAG Platform client | `src/ContractEngine.Core/Interfaces/IRagPlatformClient.cs` + `src/ContractEngine.Infrastructure/External/RagPlatformClient.cs` + `src/ContractEngine.Infrastructure/Stubs/NoOpRagPlatformClient.cs` |
| RAG extraction | `src/ContractEngine.Core/Services/ExtractionService.cs` + `ExtractionService.Pipeline.cs` (partial) + `ExtractionResultParser.cs` (helper) + `src/ContractEngine.Jobs/ExtractionProcessorJob.cs` |
| Contract diff | `src/ContractEngine.Core/Services/ContractDiffService.cs` |
| Conflict detection | `src/ContractEngine.Core/Services/ConflictDetectionService.cs` |
| Auto-renewal monitor | `src/ContractEngine.Core/Services/AutoRenewalMonitorCore.cs` + `src/ContractEngine.Jobs/AutoRenewalMonitorJob.cs` |
| Stale obligation checker | `src/ContractEngine.Core/Services/StaleObligationCheckerCore.cs` + `src/ContractEngine.Jobs/StaleObligationCheckerJob.cs` |
| Extraction defaults | `src/ContractEngine.Core/Defaults/ExtractionDefaults.cs` |
| Ecosystem clients (Phase 3) | Notification Hub: `NotificationHubClient.cs`, Workflow: `WorkflowEngineClient.cs`, Compliance: `ComplianceLedgerNatsPublisher.cs`, Invoice Recon: `InvoiceReconClient.cs`, Webhook downloader: `WebhookDocumentDownloader.cs` — all in `src/ContractEngine.Infrastructure/External/`. Interfaces in `src/ContractEngine.Core/Interfaces/{INotificationPublisher, IWorkflowTrigger, IComplianceEventPublisher, IInvoiceReconClient, IWebhookDocumentDownloader}.cs`. |
| Webhook ingestion (Batch 024) | Endpoint: `src/ContractEngine.Api/Endpoints/WebhookEndpoints.cs` (route + HMAC gate) + `WebhookEndpointHelpers.cs` (phase helpers — tenant resolve, idempotency, draft create, download chain). Parser: `src/ContractEngine.Core/Integrations/Webhooks/WebhookPayloadParser.cs`. Normalised payload: `SignedContractPayload.cs`. Downloader: `Infrastructure/External/WebhookDocumentDownloader.cs`. Operator runbook: `docs/operations/webhook-engine-setup.md`. |
| No-op stubs | `src/ContractEngine.Infrastructure/Stubs/` — `NoOpRagPlatformClient.cs`, `NoOpNotificationPublisher.cs`, `NoOpWorkflowTrigger.cs`, `NoOpCompliancePublisher.cs`, `NoOpInvoiceReconClient.cs`. |
| Ecosystem event wiring | Notification Hub: `DeadlineAlertService.cs` + `ObligationService.EmitTransitionSideEffectsAsync`. Compliance Ledger: `ObligationService` (overdue/escalated), `AutoRenewalMonitorCore` (renewed), `ContractService.TerminateAsync` (terminated). Invoice Recon: `ObligationService.Transitions.cs` (`ConfirmAsync` → `EmitInvoiceReconAsync` for `ObligationType.Payment`). |
| Background jobs | `src/ContractEngine.Jobs/` (4 jobs: DeadlineScanner, ExtractionProcessor, AutoRenewalMonitor, StaleObligationChecker) |
| DB schema/migrations | `src/ContractEngine.Infrastructure/Data/Migrations/` (9 migrations applied) |
| Entity models | `src/ContractEngine.Core/Models/` (12 entities) |
| API endpoints | `src/ContractEngine.Api/Endpoints/` (12 endpoint groups, ~41 endpoints) |
| App bootstrap | `src/ContractEngine.Api/Program.cs` |
| Integration test pattern | `tests/ContractEngine.Integration.Tests/SmokeTests/PostgresConnectivityTests.cs`, `tests/ContractEngine.Integration.Tests/Data/ContractDbContextTests.cs` |
| API middleware test pattern | `tests/ContractEngine.Api.Tests/Middleware/ExceptionHandlingMiddlewareTests.cs`, `RequestLoggingMiddlewareTests.cs`, `InMemoryLogSink.cs` |
| E2E test pattern | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` (+ 11 other `*E2ETests.cs` files) |
| Sentry PII scrubbing (Batch 025) | Filter: `src/ContractEngine.Core/Observability/SentryPrivacyFilter.cs` (zero SDK deps). Wiring: `src/ContractEngine.Api/Program.cs` (`UseSentry` + `WriteTo.Sentry` Serilog sink, both gated on non-empty `SENTRY_DSN`). Tests: `tests/ContractEngine.Core.Tests/Observability/SentryPrivacyFilterTests.cs`. |
| Notification Hub template seeder (Batch 025) | Seeder: `src/ContractEngine.Infrastructure/Data/NotificationHubTemplateSeeder.cs`. CLI: `--seed-hub-templates` handler in `Program.cs`. Runbook: `docs/operations/notification-hub-setup.md`. Uptime runbook: `docs/operations/betterstack-setup.md`. |
| CI/CD pipeline | **OUT OF SCOPE per 2026-04-17 user direction** — Batch 026 reverted (commit eee50d9). No `.github/workflows/`, no deploy scripts, no Hetzner/CI-CD runbooks exist on HEAD. Project deploys via `docker build` + manual `docker compose -f docker-compose.prod.yml up -d` on a host of the operator's choosing. |
| Test patterns | `tests/` |
