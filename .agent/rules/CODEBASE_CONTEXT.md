# Contract Lifecycle Engine — Codebase Context

> Last updated: 2026-04-16
> Template synced: 2026-04-15

## Local Dev Setup (Workstation Bootstrapping)

1. **Clone and `cd`** into the repo.
2. **Copy `.env.example` → `.env`** and leave the defaults. All integration flags default to `false` locally.
3. **Check port 5432 and 4222 availability.** If either is occupied by a sibling PostgreSQL/NATS instance, create a `docker-compose.override.yml` mapping to free host ports — e.g. `5445:5432` and `4225:4222`. Update `DATABASE_URL` and `NATS_URL` in `.env` to match. `docker-compose.override.yml` is git-ignored; each developer maintains their own.
4. **Start services:** `docker compose up -d db` (and `docker compose --profile nats up -d nats` if working on Compliance Ledger).
5. **Verify health:** `docker compose ps` should show `db` as `(healthy)`. Connect check: `docker exec contract-lifecycle-engine-db-1 pg_isready -U contract_engine`.
6. **Run the API** (once the solution exists): `dotnet run --project src/ContractEngine.Api`.

The `.env` file is git-ignored — never commit real values.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Language | C# 12 / .NET 8 |
| Framework | ASP.NET Core 8 (Minimal APIs) |
| ORM | Entity Framework Core 8 (Npgsql) |
| Database | PostgreSQL 16 |
| Background Jobs | Quartz.NET 3.x |
| Messaging | NATS.Client 2.x (JetStream) |
| HTTP Client | IHttpClientFactory (typed clients) |
| Validation | FluentValidation 11.x |
| Logging | Serilog 4.x (structured JSON) |
| Hosting | Docker on Hetzner VPS — `contracts.kingsleyonoh.com` |
| Package Manager | NuGet / dotnet CLI |
| Test Runner | xUnit 2.x + FluentAssertions + NSubstitute |
| Error Tracking | Sentry (.NET SDK) |
| Uptime | BetterStack |

## Project Structure

```
contract-lifecycle-engine/
├── src/
│   ├── ContractEngine.Api/          # ASP.NET Core host — endpoints, middleware, DI
│   │   ├── Endpoints/               # Minimal API endpoint groups
│   │   ├── Middleware/               # Tenant resolution, exception handling, logging
│   │   ├── Filters/                 # Rate limiting
│   │   └── Program.cs              # Entry point, DI registration
│   ├── ContractEngine.Core/         # Domain logic — zero external dependencies
│   │   ├── Models/                  # EF Core entities
│   │   ├── Enums/                   # Status/type enums
│   │   ├── Interfaces/              # Repository + client abstractions
│   │   ├── Services/                # Business logic, state machine
│   │   ├── Validation/              # FluentValidation validators
│   │   └── Defaults/               # Hardcoded extraction prompts
│   ├── ContractEngine.Infrastructure/ # External concerns — DB, HTTP, NATS
│   │   ├── Data/                    # ContractDbContext, migrations, seed
│   │   ├── Repositories/           # EF Core repository implementations
│   │   ├── External/               # Ecosystem integration clients
│   │   ├── Stubs/                  # No-op implementations when integrations disabled
│   │   └── Configuration/          # ServiceRegistration.cs (DI)
│   └── ContractEngine.Jobs/        # Quartz.NET background jobs
├── tests/
│   ├── ContractEngine.Core.Tests/          # Unit tests for domain logic
│   ├── ContractEngine.Api.Tests/           # In-process API integration tests (Mvc.Testing)
│   ├── ContractEngine.Integration.Tests/   # Tests with real DB + ecosystem stubs
│   └── ContractEngine.E2E.Tests/           # Real Kestrel subprocess HTTP tests
├── docs/
│   └── contract-lifecycle-engine_prd.md
├── ContractEngine.sln
├── Dockerfile
├── docker-compose.yml
├── docker-compose.override.yml       # git-ignored, per-developer port remap
└── .env.example
```

## Key Modules

| Module | Purpose | Key Files |
|--------|---------|-----------|
| Contract Management | CRUD for contracts, counterparties, documents, tags, versions | `src/ContractEngine.Core/Services/ContractService.cs` |
| Obligation Tracking | Obligation lifecycle, state machine, event sourcing | `src/ContractEngine.Core/Services/ObligationStateMachine.cs` |
| Extraction Pipeline | AI-powered obligation extraction via RAG Platform | `src/ContractEngine.Core/Services/ExtractionService.cs` |
| Deadline Alert Engine | Proactive deadline alerts, notification dispatch | `src/ContractEngine.Core/Services/DeadlineAlertService.cs` |
| Contract Analysis | Semantic diff, cross-contract conflict detection | `src/ContractEngine.Core/Services/ContractDiffService.cs` |
| Ecosystem Integration | HTTP clients + NATS publisher for 6 ecosystem services | `src/ContractEngine.Infrastructure/External/` |
| Background Jobs | Quartz.NET scheduled jobs (deadline scan, extraction, auto-renewal) | `src/ContractEngine.Jobs/` |

## Database Schema

| Table | Purpose | Key Fields |
|-------|---------|-----------|
| tenants | Multi-tenant isolation | id, api_key_hash, api_key_prefix, default_timezone |
| counterparties | Contract counterparty companies | id, tenant_id, name, legal_name, industry |
| contracts | Core contract records with lifecycle status | id, tenant_id, counterparty_id, status, end_date, auto_renewal |
| contract_versions | Version history with semantic diff | id, contract_id, version_number, diff_result (JSONB) |
| contract_documents | Uploaded contract files | id, contract_id, file_path, rag_document_id |
| contract_tags | Tagging system for contracts | tenant_id, contract_id, tag (UNIQUE) |
| obligations | Tracked contractual obligations | id, contract_id, status, next_due_date, recurrence, amount |
| obligation_events | Immutable event-sourced status history | id, obligation_id, from_status, to_status, actor (INSERT-ONLY) |
| extraction_jobs | AI extraction job tracking | id, contract_id, status, prompt_types[], raw_responses (JSONB) |
| extraction_prompts | Customizable extraction prompts per tenant | id, tenant_id, prompt_type (UNIQUE per tenant) |
| deadline_alerts | Proactive deadline and expiry alerts | id, obligation_id, alert_type, days_remaining, acknowledged |
| holiday_calendars | Business day calendar data (US, DE, UK, NL) | id, calendar_code, holiday_date, holiday_name |

> Planned tables; no EF Core migrations exist yet (Phase 0).

## External Integrations

| Service | Purpose | Auth Method |
|---------|---------|------------|
| Multi-Agent RAG Platform | AI-powered obligation extraction, semantic diff | X-API-Key via `RAG_PLATFORM_API_KEY` |
| Event-Driven Notification Hub | Email/Telegram deadline alerts | X-API-Key via `NOTIFICATION_HUB_API_KEY` |
| Webhook Ingestion Engine | DocuSign/PandaDoc signed contract ingestion | HMAC-SHA256 via `WEBHOOK_SIGNING_SECRET` |
| Workflow Automation Engine | Contract amendment approval workflows | X-API-Key via `WORKFLOW_ENGINE_API_KEY` |
| Financial Compliance Ledger | Regulatory audit trail via NATS JetStream | NATS connection (no auth) |
| Invoice Reconciliation Engine | Auto-create POs from payment obligations | X-API-Key via `INVOICE_RECON_API_KEY` |

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
| DATABASE_URL | PostgreSQL connection string (Npgsql format) | `Host=localhost;Port=5432;...` |
| PORT | API port | `5000` |
| ASPNETCORE_ENVIRONMENT | `Development` or `Production` | `Development` |
| AUTO_SEED | Auto-run seed on first boot | `true` |
| SELF_REGISTRATION_ENABLED | Allow public `POST /api/tenants/register` | `true` |
| DEFAULT_TENANT_NAME | Name of auto-seeded first tenant | `Default` |
| DOCUMENT_STORAGE_PATH | Local path for uploaded contract files | `data/documents` |
| EXTRACTION_BATCH_SIZE | Max extraction jobs per scheduler tick | `5` |
| EXTRACTION_RETRY_MAX | Max retries for failed extraction jobs | `3` |
| ALERT_WINDOWS_DAYS | CSV of days before deadline to alert | `90,30,14,7,1` |
| DEFAULT_GRACE_PERIOD_DAYS | Business days of grace after deadline | `3` |
| OVERDUE_ESCALATION_DAYS | Business days overdue before escalation | `14` |
| RAG_PLATFORM_URL / _API_KEY / _ENABLED | RAG Platform connection | `_ENABLED=false` locally |
| NOTIFICATION_HUB_URL / _API_KEY / _ENABLED | Notification Hub connection | `_ENABLED=false` |
| WEBHOOK_SIGNING_SECRET / WEBHOOK_ENGINE_ENABLED | Webhook signature verification | `_ENABLED=false` |
| WORKFLOW_ENGINE_URL / _API_KEY / _ENABLED | Workflow Engine connection | `_ENABLED=false` |
| NATS_URL / COMPLIANCE_LEDGER_ENABLED | NATS JetStream for Compliance Ledger | `_ENABLED=false` |
| INVOICE_RECON_URL / _API_KEY / _ENABLED | Invoice Recon connection | `_ENABLED=false` |
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
| Docker compose | `docker compose up -d` |

## Tenant Model

- **Isolation:** API key per tenant, resolved via `X-API-Key` header
- **Scoping:** `tenant_id` on every data table, enforced by EF Core global query filter
- **Key Format:** `cle_live_{32_hex_chars}` — SHA-256 hashed for storage
- **Middleware:** `TenantResolutionMiddleware` resolves key → tenant → request context
- **Registration:** `POST /api/tenants/register` (guarded by `SELF_REGISTRATION_ENABLED`)

## Key Patterns & Conventions

### Architectural Principles (always on)

- **Minimal APIs:** Endpoint groups in `Endpoints/` classes, not MVC controllers. Keep handlers thin — validate, call a service, format the response.
- **Clean architecture:** `Core` (domain) → `Infrastructure` (data/external) → `Api` (composition root) → `Jobs` (scheduled work). Never import upward. `Core` has ZERO external dependencies.
- **Feature-flagged integrations:** Each ecosystem service behind `{SERVICE}_ENABLED` env var. Real client or no-op stub registered at startup.
- **Event sourcing (obligations):** Every status change → immutable `obligation_events` insert. No UPDATE/DELETE on events table.
- **Extract-then-confirm:** AI-extracted obligations always `pending` until human confirms. No auto-activation.
- **Async/Await:** All I/O operations async. Methods suffixed with `Async`.
- **Import order:** `System.*` → third-party packages → local project namespaces, blank line between groups.

### 1. Error Response Envelope (PRD Section 8b)

Every non-2xx response emitted by the API MUST use this shape — no exceptions, including for 404s and 422 validation errors. The shape is enforced by `ExceptionHandlingMiddleware` (to be implemented); service code throws typed exceptions (`ValidationException`, `NotFoundException`, `ConflictException`, etc.) and the middleware serialises them.

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

Allowed codes: `VALIDATION_ERROR`, `NOT_FOUND`, `CONFLICT`, `UNAUTHORIZED`, `FORBIDDEN`, `RATE_LIMITED`, `INTERNAL_ERROR`, `SERVICE_UNAVAILABLE`. `details` is optional (omit for 404, required for 422). `request_id` is minted per-request and echoed into logs and responses — use a middleware-scoped value so logs and the client see the same ID.

### 2. Cursor-Based Pagination (PRD Section 8b)

All list endpoints use **composite cursors** built from `(created_at, id)`, base64-encoded. No page numbers, no offset. Default page size **25**, max **100**. A shared helper in `Core` (to be implemented at `ContractEngine.Core/Pagination/Cursor.cs`) encodes/decodes cursors and applies the WHERE clause `(created_at, id) < (cursor.created_at, cursor.id)` (DESC) or `>` (ASC).

Query parameters: `?cursor=<opaque>&limit=<1-100>&sort_dir=asc|desc`.

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

1. **`TenantResolutionMiddleware`** reads the `X-API-Key` header. Missing → `401 UNAUTHORIZED`.
2. Key format is `cle_live_{32_hex_chars}`. Reject malformed keys at the middleware.
3. Compute `SHA-256(apiKey)` → hex string.
4. Query `tenants` WHERE `api_key_hash = @hash AND is_active = true`. Not found / inactive → `401`.
5. Build an `ITenantContext` (scoped service) carrying `TenantId`, `DefaultTimezone`, `DefaultCurrency` and store it on `HttpContext.Items` / DI.
6. All downstream services and the `DbContext` read from `ITenantContext`.

**Public endpoints** (no key required): `POST /api/tenants/register` (when `SELF_REGISTRATION_ENABLED=true`), `GET /health`, `GET /health/db`, `GET /health/ready`, `POST /api/webhooks/contract-signed` (HMAC-verified separately).

### 4. EF Core Global Query Filter for Tenant Isolation (PRD Section 5.1)

Every entity that has a `TenantId` column gets a global query filter registered in `ContractDbContext.OnModelCreating`:

```csharp
modelBuilder.Entity<Contract>()
    .HasQueryFilter(c => c.TenantId == _tenantContext.TenantId);
```

The `DbContext` takes `ITenantContext` in its constructor. This ensures `context.Contracts.ToList()` already filters to the current tenant — no hand-written `.Where(c => c.TenantId == ...)` in services. For jobs and seed scripts that need cross-tenant access, inject a `CrossTenantDbContext` variant (factory pattern) that bypasses the filter via `IgnoreQueryFilters()`.

Entities with `TenantId`: `Tenant` (self), `Counterparty`, `Contract`, `ContractVersion`, `ContractDocument`, `ContractTag`, `Obligation`, `ObligationEvent`, `DeadlineAlert`, `ExtractionJob`, `ExtractionPrompt`, `HolidayCalendar` (nullable for system-wide calendars).

### 5. FluentValidation Pipeline Integration (PRD Section 3, 5.1)

All request DTOs have a matching `AbstractValidator<T>` in `ContractEngine.Core/Validation/`. Validators are auto-registered by assembly scan in `ServiceRegistration.cs`:

```csharp
services.AddValidatorsFromAssemblyContaining<ContractValidator>();
```

Minimal-API endpoints apply a reusable endpoint filter (`ValidationFilter<TRequest>`) that resolves `IValidator<TRequest>`, validates, and on failure throws a `ValidationException` carrying the field errors. `ExceptionHandlingMiddleware` converts that to the `VALIDATION_ERROR` envelope above.

Validators may take `ITenantContext` for tenant-scoped uniqueness checks (e.g., reference_number uniqueness within a tenant).

### 6. Serilog Structured JSON Logging (PRD Section 9, 10b)

Single sink: stdout, JSON formatter. Configured at startup in `Program.cs` via `Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration))` backed by `appsettings.json`. Min level from `LOG_LEVEL` env var.

**Mandatory enrichment** (added via middleware or Serilog enrichers):

- `request_id` — same value returned in the error envelope; generated per request
- `tenant_id` — resolved by `TenantResolutionMiddleware`; null for public endpoints
- `module` — logical area (`contracts`, `obligations`, `extraction`, `jobs`, etc.)
- `environment` — `Development` / `Production`

**Never log:** raw API keys, `X-API-Key` header values, signed JWTs, counterparty contact emails, contract full text, uploaded document binary.

**Do log:** business events (`obligation.status_changed`), scheduled job outcomes (`deadline_scan.completed`), external integration call latency, request latency p95, exceptions with stack traces (via Serilog `.ForContext<T>()`).

---

- **Business day math:** `BusinessDayCalculator` uses holiday calendars with timezone-aware dates. Configurable grace periods per obligation.

## Gotchas & Lessons Learned

> Discovered during implementation. Added automatically by `/implement-next` Step 9.3.

| Date | Area | Gotcha | Discovered In |
|------|------|--------|---------------|
| 2026-04-04 | PostgreSQL/Docker | Local port 5432 may conflict with system PostgreSQL. Map Docker to alternate port (e.g., 5440:5432) in docker-compose.override.yml. Update DATABASE_URL accordingly. | Swarm Intelligence Gateway |
| 2026-04-16 | Docker Compose | Sibling projects on this workstation occupy 5432, 5433, 5434, 5435, 5436, 5440, 5441, 5442, 5443, 5444, 5450 (Postgres) and 4222, 4223, 4224 (NATS). Contract Lifecycle Engine uses **5445** (Postgres) and **4225/8225** (NATS) via `docker-compose.override.yml`. Override uses the `!override` tag so port lists replace, not append. | Batch 001 |
| 2026-04-16 | Docker Compose | The `version: "3.8"` header is obsolete in Compose v2+ and emits a warning. Remove it from both `docker-compose.yml` and `docker-compose.override.yml`. | Batch 001 |
| 2026-04-16 | Platform | .NET 8 / C# 12 is the committed target (not .NET 9 / C# 13 as originally pinned in the PRD). The host SDK is 8.0.400; PRD, Dockerfile (`sdk:8.0` / `aspnet:8.0`), llms.txt, agent rules, workflows, and session-continuity files (CLAUDE.md / AGENTS.md / .cursorrules) all now say .NET 8. Always check host SDK before pinning a framework in a PRD. | Batch 002 |
| 2026-04-16 | Windows / subprocess | When launching a long-running .NET process with `RedirectStandardOutput` + `RedirectStandardError`, you MUST drain both streams via `BeginOutputReadLine` / `BeginErrorReadLine` **before** the poll loop starts, or the child stalls once the ~64KB Windows pipe buffer fills. Reading `StandardError` synchronously at the end does not help — Kestrel has already blocked by then. Pattern is codified in `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs`. | Batch 002 |
| 2026-04-16 | dotnet CLI | `dotnet add reference` does NOT support `--no-restore` (unlike `dotnet add package`). Omit the flag for project-reference adds; they're cheap and deferred-restored anyway. | Batch 002 |

## Shared Foundation (MUST READ before any implementation)

> These files define the project's shared patterns, configuration, and utilities.
> The AI MUST read these **in full** before writing ANY new code.
> Some files do not yet exist (project is in Phase 0). Until they do, the **Binding Specs** column below is the contract; new code must conform to the referenced `CODEBASE_CONTEXT.md` / PRD sections and establish the file when the relevant batch ships.

| Category | File(s) | Status | Binding Specs |
|----------|---------|--------|---------------|
| DB context | `src/ContractEngine.Infrastructure/Data/ContractDbContext.cs` | planned | CODEBASE_CONTEXT `Key Patterns §4` (EF Core global query filter) |
| DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` | planned | CODEBASE_CONTEXT `Key Patterns §5` (FluentValidation assembly scan); PRD §5.6 (feature-flagged integrations) |
| Error handling | `src/ContractEngine.Api/Middleware/ExceptionHandlingMiddleware.cs` | planned | CODEBASE_CONTEXT `Key Patterns §1` (error envelope); PRD §8b |
| Tenant resolution | `src/ContractEngine.Api/Middleware/TenantResolutionMiddleware.cs` | planned | CODEBASE_CONTEXT `Key Patterns §3` (X-API-Key → SHA-256 → tenant context); PRD §8b Authentication |
| Pagination helper | `src/ContractEngine.Core/Pagination/Cursor.cs` | planned | CODEBASE_CONTEXT `Key Patterns §2` (composite cursor, default 25, max 100) |
| Request logging | `src/ContractEngine.Api/Middleware/RequestLoggingMiddleware.cs` | planned | CODEBASE_CONTEXT `Key Patterns §6` (Serilog JSON, request_id/tenant_id/module enrichers) |
| Business day calc | `src/ContractEngine.Core/Services/BusinessDayCalculator.cs` | planned | PRD §5.4 |
| State machine | `src/ContractEngine.Core/Services/ObligationStateMachine.cs` | planned | PRD §4.6 transition map, §5.3 |
| Extraction defaults | `src/ContractEngine.Core/Defaults/ExtractionDefaults.cs` | planned | PRD §5.2 |
| Seed data | `src/ContractEngine.Infrastructure/Data/SeedData.cs` | planned | PRD §11 |
| App entry point | `src/ContractEngine.Api/Program.cs` | present | PRD §9 — Minimal API host with Serilog bootstrap logger + `/health` endpoint; full DI wiring pending later batches |
| Local services | `docker-compose.yml` + `docker-compose.override.yml` | present | PostgreSQL 16 on host 5445, NATS 2 on host 4225 (profile `nats`) |
| Environment catalogue | `.env.example` | present | Complete env var list (committed); `.env` = local values (git-ignored) |
| E2E server fixture | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` | present | Real Kestrel subprocess pattern: spawn compiled DLL → drain stdout/stderr → wait for TCP bind → assert HTTP. All future E2E tests follow this shape. |

## Deep References

| Topic | Where to look |
|-------|--------------|
| Contract lifecycle | `src/ContractEngine.Core/Services/ContractService.cs` |
| Obligation state machine | `src/ContractEngine.Core/Services/ObligationStateMachine.cs` |
| RAG extraction | `src/ContractEngine.Core/Services/ExtractionService.cs` |
| Deadline alerts | `src/ContractEngine.Core/Services/DeadlineAlertService.cs` |
| Ecosystem clients | `src/ContractEngine.Infrastructure/External/` |
| No-op stubs | `src/ContractEngine.Infrastructure/Stubs/` |
| Background jobs | `src/ContractEngine.Jobs/` |
| DB schema/migrations | `src/ContractEngine.Infrastructure/Data/Migrations/` |
| Entity models | `src/ContractEngine.Core/Models/` |
| API endpoints | `src/ContractEngine.Api/Endpoints/` |
| App bootstrap | `src/ContractEngine.Api/Program.cs` |
| Integration test pattern | `tests/ContractEngine.Integration.Tests/PostgresConnectivityTests.cs` |
| E2E test pattern | `tests/ContractEngine.E2E.Tests/HealthEndpointTests.cs` |
| Test patterns | `tests/` |
