# Contract Lifecycle Engine — Codebase Context

> Last updated: 2026-04-15
> Template synced: 2026-04-15

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
│   ├── ContractEngine.Core.Tests/   # Unit tests for domain logic
│   ├── ContractEngine.Api.Tests/    # Integration tests for API endpoints
│   └── ContractEngine.Integration.Tests/ # Tests with real DB + ecosystem stubs
├── docs/
│   └── contract-lifecycle-engine_prd.md
├── ContractEngine.sln
├── Dockerfile
├── docker-compose.yml
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

| Variable | Purpose | Source |
|----------|---------|--------|
| DATABASE_URL | PostgreSQL connection string (Npgsql format) | .env |
| PORT | API port (default: 5000) | .env |
| ASPNETCORE_ENVIRONMENT | Development / Production | .env |
| AUTO_SEED | Auto-run seed on first boot | .env |
| SELF_REGISTRATION_ENABLED | Allow public tenant registration | .env |
| DOCUMENT_STORAGE_PATH | Local path for uploaded contract files | .env |
| RAG_PLATFORM_URL / _API_KEY / _ENABLED | RAG Platform connection | .env |
| NOTIFICATION_HUB_URL / _API_KEY / _ENABLED | Notification Hub connection | .env |
| WEBHOOK_SIGNING_SECRET / WEBHOOK_ENGINE_ENABLED | Webhook signature verification | .env |
| WORKFLOW_ENGINE_URL / _API_KEY / _ENABLED | Workflow Engine connection | .env |
| NATS_URL / COMPLIANCE_LEDGER_ENABLED | NATS JetStream for Compliance Ledger | .env |
| INVOICE_RECON_URL / _API_KEY / _ENABLED | Invoice Recon connection | .env |
| SENTRY_DSN | Sentry error tracking DSN | .env |
| LOG_LEVEL | Serilog minimum level | .env |

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

- **Minimal APIs:** Endpoint groups in `Endpoints/` classes, not MVC controllers
- **Clean architecture:** Core (domain) → Infrastructure (data/external) → Api (composition root). Never import upward.
- **Feature-flagged integrations:** Each ecosystem service behind `{SERVICE}_ENABLED` env var. Real client or no-op stub registered at startup.
- **Event sourcing (obligations):** Every status change → immutable `obligation_events` insert. No UPDATE/DELETE on events table.
- **Extract-then-confirm:** AI-extracted obligations always `pending` until human confirms. No auto-activation.
- **Cursor-based pagination:** `created_at` + `id` composite cursor (base64-encoded). Default 25, max 100.
- **Error response format:** `{ "error": { "code", "message", "details[]", "request_id" } }`
- **Business day math:** `BusinessDayCalculator` uses holiday calendars with timezone-aware dates. Configurable grace periods.
- **Async/Await:** All I/O operations async. Methods suffixed with `Async`.
- **Import order:** System → third-party → local project

## Gotchas & Lessons Learned

> Discovered during implementation. Added automatically by `/implement-next` Step 9.3.

| Date | Area | Gotcha | Discovered In |
|------|------|--------|---------------|
| 2026-04-04 | PostgreSQL/Docker | Local port 5432 may conflict with system PostgreSQL. Map Docker to alternate port (e.g., 5440:5432) in docker-compose.override.yml. Update DATABASE_URL accordingly. | Swarm Intelligence Gateway |

## Shared Foundation (MUST READ before any implementation)

> These files define the project's shared patterns, configuration, and utilities.
> The AI MUST read these **in full** before writing ANY new code.

| Category | File(s) | What it establishes |
|----------|---------|-------------------|
| DB context | `src/ContractEngine.Infrastructure/Data/ContractDbContext.cs` | EF Core context with global tenant query filter |
| DI registration | `src/ContractEngine.Infrastructure/Configuration/ServiceRegistration.cs` | All service/client/job registration |
| Error handling | `src/ContractEngine.Api/Middleware/ExceptionHandlingMiddleware.cs` | Global error → JSON error response |
| Tenant resolution | `src/ContractEngine.Api/Middleware/TenantResolutionMiddleware.cs` | X-API-Key → tenant context |
| Business day calc | `src/ContractEngine.Core/Services/BusinessDayCalculator.cs` | Business day arithmetic with holidays |
| State machine | `src/ContractEngine.Core/Services/ObligationStateMachine.cs` | Obligation status transition rules |
| Extraction defaults | `src/ContractEngine.Core/Defaults/ExtractionDefaults.cs` | Fallback extraction prompts |
| Seed data | `src/ContractEngine.Infrastructure/Data/SeedData.cs` | First-run tenant + holiday seeding |
| App entry point | `src/ContractEngine.Api/Program.cs` | Minimal API setup, middleware pipeline, DI |

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
| Test patterns | `tests/` |
