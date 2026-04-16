# Contract Lifecycle Engine — Codebase Context

> Last updated: 2026-04-16 (Phase 1 close)
> Template synced: 2026-04-15
> Status: **Phase 1 complete — 53/53 items shipped. Phase 2 (AI Extraction) is next.**

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
│   │   ├── Endpoints/               # 10 endpoint groups: Tenants, Counterparties, Contracts,
│   │   │                             #   ContractDocuments, ContractTags, ContractVersions,
│   │   │                             #   Obligations, Alerts, Analytics, Health
│   │   ├── Endpoints/Dto/           # Request / response DTOs
│   │   ├── Middleware/              # ExceptionHandling, RequestLogging, TenantResolution
│   │   ├── RateLimiting/            # Rate-limit policies + configuration
│   │   └── Program.cs              # Entry point, DI registration, --seed CLI, AUTO_MIGRATE
│   ├── ContractEngine.Core/         # Domain logic — zero external dependencies
│   │   ├── Abstractions/            # ITenantContext, ITenantScoped, NullTenantContext
│   │   ├── Models/                  # 10 EF Core entities (Tenant, Counterparty, Contract,
│   │   │                             #   ContractDocument, ContractTag, ContractVersion,
│   │   │                             #   Obligation, ObligationEvent, DeadlineAlert, HolidayCalendar)
│   │   ├── Enums/                   # 9 enums (ContractStatus/Type, 5 Obligation enums, AlertType,
│   │   │                             #   DisputeResolution)
│   │   ├── Exceptions/              # EntityTransitionException + Contract/Obligation subclasses
│   │   ├── Interfaces/              # Repository + client abstractions
│   │   ├── Services/                # Business logic, state machine, scanner core, analytics
│   │   ├── Pagination/              # PaginationCursor, PageRequest, PagedResult<T>, IHasCursor
│   │   └── Validation/              # FluentValidation validators
│   ├── ContractEngine.Infrastructure/ # External concerns — DB, storage, tenancy, analytics
│   │   ├── Data/                    # ContractDbContext, migrations, FirstRunSeeder, HolidayCalendarSeeder
│   │   ├── Data/Migrations/         # 8 EF Core migrations (tenants → deadline_alerts)
│   │   ├── Repositories/            # EF Core repository implementations
│   │   ├── Storage/                 # LocalDocumentStorage
│   │   ├── Tenancy/                 # TenantContextAccessor (scoped ITenantContext writer)
│   │   ├── Analytics/               # EfAnalyticsQueryContext
│   │   ├── Jobs/                    # DeadlineScanStore, DeadlineAlertWriter
│   │   ├── Pagination/              # CursorPaginationExtensions
│   │   └── Configuration/           # ServiceRegistration.cs (DI)
│   └── ContractEngine.Jobs/         # Quartz.NET background jobs
│       ├── DeadlineScannerJob.cs    # Hourly scanner (cron 0 0 * * * ?)
│       └── ServiceRegistration.cs   # Quartz DI, JOBS_ENABLED gate
├── tests/
│   ├── ContractEngine.Core.Tests/          # Unit tests — domain logic (250 tests)
│   ├── ContractEngine.Api.Tests/           # In-process API tests via WebApplicationFactory (154)
│   ├── ContractEngine.Integration.Tests/   # Real DB + ecosystem stubs via DatabaseFixture (138)
│   └── ContractEngine.E2E.Tests/           # Real Kestrel subprocess HTTP tests (12)
├── docs/
│   ├── contract-lifecycle-engine_prd.md
│   └── progress.md
├── ContractEngine.sln
├── Dockerfile                        # Multi-stage — sdk:8.0 build → aspnet:8.0 runtime, 334 MB
├── docker-compose.yml                # Dev — Postgres 16 on 5445, NATS on 4225 (profile)
├── docker-compose.override.yml       # git-ignored, per-developer port remap
├── docker-compose.prod.yml           # Prod — GHCR image, Caddy reverse proxy labels
├── .dockerignore                     # Excludes tests/, bin/, obj/ from build context
└── .env.example                      # Committed env var catalogue
```

**Test totals (Phase 1 close): 566 passing (554 non-E2E + 12 E2E).**

## Key Modules

| Module | Purpose | Key Files |
|--------|---------|-----------|
| Tenant Management | Multi-tenant isolation, API key auth, registration, self-serve profile | `src/ContractEngine.Core/Services/TenantService.cs`, `Api/Middleware/TenantResolutionMiddleware.cs` |
| Counterparty Management | Contract counterparty CRUD + search | `src/ContractEngine.Core/Services/CounterpartyService.cs` |
| Contract Management | CRUD + lifecycle (draft → active → terminated/archived), auto-counterparty | `src/ContractEngine.Core/Services/ContractService.cs` |
| Contract Documents | Multipart upload, local file storage, download streaming | `src/ContractEngine.Core/Services/ContractDocumentService.cs`, `Infrastructure/Storage/LocalDocumentStorage.cs` |
| Contract Tags & Versions | Tag replacement (REPLACE semantics), version history | `src/ContractEngine.Core/Services/ContractTagService.cs`, `ContractVersionService.cs` |
| Obligation Tracking | State machine, event sourcing, recurrence spawn, archive cascade | `src/ContractEngine.Core/Services/ObligationStateMachine.cs`, `ObligationService.cs` |
| Business Day Calculator | Holiday-aware business day math (US/DE/UK/NL) | `src/ContractEngine.Core/Services/BusinessDayCalculator.cs` |
| Deadline Alert Engine | Idempotent alert creation, bulk acknowledge | `src/ContractEngine.Core/Services/DeadlineAlertService.cs` |
| Deadline Scanner | Hourly Quartz job — auto-transition + alerts | `src/ContractEngine.Core/Services/DeadlineScannerCore.cs`, `Jobs/DeadlineScannerJob.cs` |
| Analytics | Dashboard + 3 aggregations (by type, value, calendar) | `src/ContractEngine.Core/Services/AnalyticsService.cs` |
| Health | ASP.NET + DB + integration readiness probes | `src/ContractEngine.Api/Endpoints/HealthEndpoints.cs` |
| Extraction Pipeline | AI-powered obligation extraction via RAG Platform | **Phase 2** (`src/ContractEngine.Core/Services/ExtractionService.cs`) |
| Contract Analysis | Semantic diff, cross-contract conflict detection | **Phase 2** (`src/ContractEngine.Core/Services/ContractDiffService.cs`) |
| Ecosystem Integration | HTTP clients + NATS publisher for 6 ecosystem services | **Phase 3** (`src/ContractEngine.Infrastructure/External/`) |

## Database Schema (Phase 1 — 10 tables, 8 migrations applied)

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

**Phase 2 tables (planned):** `extraction_jobs`, `extraction_prompts` (both tenant-scoped; `extraction_jobs` gets indexes `(tenant_id, status)` + `(tenant_id, contract_id)`).

## External Integrations

| Service | Purpose | Auth Method | Phase |
|---------|---------|------------|-------|
| Multi-Agent RAG Platform | AI-powered obligation extraction, semantic diff | X-API-Key via `RAG_PLATFORM_API_KEY` | Phase 2 |
| Event-Driven Notification Hub | Email/Telegram deadline alerts | X-API-Key via `NOTIFICATION_HUB_API_KEY` | Phase 3 |
| Webhook Ingestion Engine | DocuSign/PandaDoc signed contract ingestion | HMAC-SHA256 via `WEBHOOK_SIGNING_SECRET` | Phase 3 |
| Workflow Automation Engine | Contract amendment approval workflows | X-API-Key via `WORKFLOW_ENGINE_API_KEY` | Phase 3 |
| Financial Compliance Ledger | Regulatory audit trail via NATS JetStream | NATS connection (no auth) | Phase 3 |
| Invoice Reconciliation Engine | Auto-create POs from payment obligations | X-API-Key via `INVOICE_RECON_API_KEY` | Phase 3 |

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
| Contract | `src/ContractEngine.Core/Models/Contract.cs`, `Enums/ContractStatus.cs`, `ContractType.cs`, `Services/ContractService.cs`, `Interfaces/IContractRepository.cs` + `Infrastructure/Repositories/ContractRepository.cs`, `Api/Endpoints/ContractEndpoints.cs` | present | PRD §4.3 transition map enforced by `ActivateAsync`, `TerminateAsync`, `ArchiveAsync`. `ArchiveAsync` delegates to `ObligationService.ExpireDueToContractArchiveAsync` (obligation cascade) for all non-terminal obligations. Invalid transitions throw `ContractTransitionException` → 422 INVALID_TRANSITION. JSON enum policy `JsonStringEnumConverter(SnakeCaseLower)` in `Program.cs`. |
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
| App entry point | `src/ContractEngine.Api/Program.cs` | present | Minimal API host. Serilog bootstrap with test-supplied logger detection. `--seed` CLI short-circuit. `AUTO_MIGRATE` (default true) runs `Database.MigrateAsync()` before pipeline starts; `Testing` environment short-circuits. `AUTO_SEED` (default true) populates holiday calendars + first-run tenant. Middleware order: ExceptionHandling → RequestLogging → TenantResolution → RateLimiter → routes. Registers 10 endpoint groups. JSON enum policy = `SnakeCaseLower`. |
| Local services | `docker-compose.yml` + `docker-compose.override.yml` | present | PostgreSQL 16 on host 5445, NATS 2 on host 4225 (profile `nats`) |
| Production deployment | `docker-compose.prod.yml` + `Dockerfile` + `.dockerignore` | present | GHCR image `ghcr.io/kingsleyonoh/contract-lifecycle-engine:latest`, Caddy reverse proxy labels for `contracts.kingsleyonoh.com`, `documents` named volume persisted at `/app/data/documents`. Multi-stage Dockerfile (`sdk:8.0` build → `aspnet:8.0` runtime, 334 MB). `.dockerignore` excludes `tests/` + `bin/` + `obj/`. |
| Environment catalogue | `.env.example` | present | Complete env var list (committed); `.env` = local values (git-ignored). Batch 018 added `AUTO_MIGRATE=true`. |
| E2E server fixture | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` | present | Real Kestrel subprocess pattern: spawn compiled DLL → drain stdout/stderr → wait for TCP bind → assert HTTP. All 11 other E2E test classes follow this shape across ports 5050–5061. |
| API middleware test infrastructure | `tests/ContractEngine.Api.Tests/Middleware/InMemoryLogSink.cs`, `SerilogTestBootstrap.cs`, `WebApplicationCollection.cs` | present | Process-wide `ILogEventSink` with `Clear()` / `Snapshot()`. Idempotent Serilog bootstrap. `[CollectionDefinition("WebApplication", DisableParallelization = true)]` for xUnit serialisation of factory-using test classes. |
| Integration test fixture | `tests/ContractEngine.Integration.Tests/Fixtures/DatabaseFixture.cs` | present | Scoped to `contract_engine_test` DB. `InitializeAsync` runs `Database.MigrateAsync` + `HolidayCalendarSeeder.SeedAsync` on first instantiation; per-test cleanup via repository calls. |

## Deep References

| Topic | Where to look |
|-------|--------------|
| Contract lifecycle | `src/ContractEngine.Core/Services/ContractService.cs` |
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
| RAG extraction | **Phase 2** — `src/ContractEngine.Core/Services/ExtractionService.cs` |
| Ecosystem clients | **Phase 3** — `src/ContractEngine.Infrastructure/External/` |
| No-op stubs | **Phase 3** — `src/ContractEngine.Infrastructure/Stubs/` |
| Background jobs | `src/ContractEngine.Jobs/` |
| DB schema/migrations | `src/ContractEngine.Infrastructure/Data/Migrations/` (8 migrations applied) |
| Entity models | `src/ContractEngine.Core/Models/` (10 entities) |
| API endpoints | `src/ContractEngine.Api/Endpoints/` (10 endpoint groups, ~35 endpoints) |
| App bootstrap | `src/ContractEngine.Api/Program.cs` |
| Integration test pattern | `tests/ContractEngine.Integration.Tests/SmokeTests/PostgresConnectivityTests.cs`, `tests/ContractEngine.Integration.Tests/Data/ContractDbContextTests.cs` |
| API middleware test pattern | `tests/ContractEngine.Api.Tests/Middleware/ExceptionHandlingMiddlewareTests.cs`, `RequestLoggingMiddlewareTests.cs`, `InMemoryLogSink.cs` |
| E2E test pattern | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` (+ 11 other `*E2ETests.cs` files) |
| Test patterns | `tests/` |
