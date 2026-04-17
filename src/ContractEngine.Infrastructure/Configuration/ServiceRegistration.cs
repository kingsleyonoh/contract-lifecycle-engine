using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using ContractEngine.Infrastructure.Analytics;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.External;
using ContractEngine.Infrastructure.Repositories;
using ContractEngine.Infrastructure.Storage;
using ContractEngine.Infrastructure.Stubs;
using ContractEngine.Infrastructure.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ContractEngine.Infrastructure.Configuration;

/// <summary>
/// Extension methods for registering the Infrastructure layer (EF Core DbContext, repositories,
/// services, tenant context, validators) into the ASP.NET Core DI container.
/// </summary>
public static class ServiceRegistration
{
    private const string LocalDevConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine;Username=contract_engine;Password=localdev";

    /// <summary>
    /// Registers Contract Engine infrastructure services. After Batch 004 the tenant context is
    /// a scoped <see cref="TenantContextAccessor"/> that the resolution middleware writes to —
    /// unresolved requests (public endpoints, webhooks pre-verification) still see
    /// <c>IsResolved=false</c>, matching the previous <see cref="NullTenantContext"/> behaviour.
    /// </summary>
    public static IServiceCollection AddContractEngineInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);
        services.AddDbContext<ContractDbContext>(options => options.UseNpgsql(connectionString));

        // One scoped TenantContextAccessor per request, aliased to ITenantContext for consumers.
        services.AddScoped<TenantContextAccessor>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextAccessor>());

        // Tenant data access + service layer.
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<TenantService>();

        // Counterparty data access + service layer.
        services.AddScoped<ICounterpartyRepository, CounterpartyRepository>();
        services.AddScoped<CounterpartyService>();

        // Contract data access + service layer (Batch 007).
        services.AddScoped<IContractRepository, ContractRepository>();
        services.AddScoped<ContractService>();

        // Contract document storage + data access + service layer (Batch 009).
        // Storage is a singleton — it holds only the resolved root path and is safe to share.
        services.AddSingleton<IDocumentStorage, LocalDocumentStorage>();
        services.AddScoped<IContractDocumentRepository, ContractDocumentRepository>();
        services.AddScoped<ContractDocumentService>();

        // Contract tags + contract versions (Batch 010).
        services.AddScoped<IContractTagRepository, ContractTagRepository>();
        services.AddScoped<ContractTagService>();
        services.AddScoped<IContractVersionRepository, ContractVersionRepository>();
        services.AddScoped<ContractVersionService>();

        // Obligation foundation (Batch 011) — entities, state machine, repositories.
        // Batch 012 adds the service orchestration layer + endpoints on top.
        // The state machine is stateless (no fields, no deps) so singleton lifetime is safe and
        // avoids allocating a fresh instance per scope.
        services.AddSingleton<ObligationStateMachine>();
        services.AddScoped<IObligationRepository, ObligationRepository>();
        services.AddScoped<IObligationEventRepository, ObligationEventRepository>();
        services.AddScoped<ObligationService>();

        // Deadline alerts (Batch 015) — entity, repository, service. The scanner job that fills
        // the table lands in Batch 016; today the repository is populated manually (tests) or by
        // future callers. Scoped lifetimes mirror the obligation slice.
        services.AddScoped<IDeadlineAlertRepository, DeadlineAlertRepository>();
        services.AddScoped<DeadlineAlertService>();

        // Analytics (Batch 017) — read-only aggregations. The query surface lives behind
        // IAnalyticsQueryContext so AnalyticsService stays in Core. Scoped lifetimes — the query
        // context holds a DbContext reference.
        services.AddScoped<IAnalyticsQueryContext, EfAnalyticsQueryContext>();
        services.AddScoped<AnalyticsService>();

        // Business-day / holiday calendar (Batch 014). Calculator is a stateless singleton backed
        // by the in-memory cache. Repository is scoped because it holds a DbContext — the
        // calculator reaches it through a factory so each cache-miss gets a fresh scope.
        services.AddMemoryCache();
        services.AddScoped<IHolidayCalendarRepository, HolidayCalendarRepository>();
        services.AddSingleton<IHolidayCalendarRepositoryFactory, HolidayCalendarRepositoryFactory>();
        services.AddSingleton<IBusinessDayCalculator, BusinessDayCalculator>();

        // FluentValidation — register validators by assembly scan (Core). New validators
        // placed under ContractEngine.Core.Validation are picked up automatically.
        services.AddValidatorsFromAssemblyContaining<RegisterTenantRequestValidator>();

        // Extraction pipeline data layer (Batch 020) — prompts + jobs.
        services.AddScoped<IExtractionPromptRepository, ExtractionPromptRepository>();
        services.AddScoped<IExtractionJobRepository, ExtractionJobRepository>();

        // Extraction service (Batch 021) — orchestrates the RAG extraction pipeline.
        services.AddScoped<ExtractionService>();

        // Contract analysis (Batch 022) — diff and conflict detection via RAG.
        services.AddScoped<ContractDiffService>();
        services.AddScoped<ConflictDetectionService>();

        // RAG Platform integration (Batch 019) — feature-flagged per PRD §5.6a.
        // ENABLED=true  → typed HttpClient with retry + circuit breaker resilience pipeline.
        // ENABLED=false → NoOp stub; reads return empty, writes throw (see NoOpRagPlatformClient).
        AddRagPlatformClient(services, configuration);

        // Phase 3 ecosystem integrations (Batch 023) — all four share the RAG pattern:
        // feature-flagged, typed HttpClient + resilience when enabled, no-op stub when not.
        // Compliance Ledger swaps HTTP for NATS JetStream but follows the same flag contract.
        AddNotificationHub(services, configuration);
        AddWorkflowEngine(services, configuration);
        AddInvoiceRecon(services, configuration);
        AddComplianceLedger(services, configuration);

        // Webhook Engine INBOUND integration (Batch 024). The endpoint lives in the API layer; the
        // infrastructure side just owns the document downloader that streams the signed PDF from
        // DocuSign / PandaDoc. Registered whenever the webhook endpoint is enabled — the endpoint
        // guards its own 404 fallback when WEBHOOK_ENGINE_ENABLED=false.
        AddWebhookEngine(services, configuration);

        // Parser is a pure-function stateless singleton — no DI deps.
        services.AddSingleton<ContractEngine.Core.Integrations.Webhooks.WebhookPayloadParser>();

        return services;
    }

    /// <summary>
    /// Wires the RAG Platform client into DI. Split out of the main registration method so the
    /// resilience config stays readable. See <c>RagPlatformClient</c> for the wire-level details.
    /// </summary>
    private static void AddRagPlatformClient(IServiceCollection services, IConfiguration configuration)
    {
        var ragEnabled = configuration.GetValue("RAG_PLATFORM_ENABLED", defaultValue: false);
        if (!ragEnabled)
        {
            services.AddSingleton<IRagPlatformClient, NoOpRagPlatformClient>();
            return;
        }

        var baseUrl = configuration.GetValue<string>("RAG_PLATFORM_URL")
            ?? throw new InvalidOperationException(
                "RAG_PLATFORM_URL is required when RAG_PLATFORM_ENABLED=true.");

        services
            .AddHttpClient<IRagPlatformClient, RagPlatformClient>(client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddResilienceHandler("rag-platform", builder =>
            {
                // Retry budget: 3 attempts at 1s → 3s → 9s exponential (matches PRD §5.6a).
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = false,
                });

                // Circuit breaker: open after 5 consecutive failures, stay open for 30s.
                // FailureRatio=1.0 + MinimumThroughput=5 encodes "5 in a row" without a sliding-window
                // percentage — simpler reasoning for a low-traffic integration.
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    SamplingDuration = TimeSpan.FromSeconds(30),
                });
            });
    }

    /// <summary>
    /// Wires the Notification Hub client (PRD §5.6b). Same shape as the RAG client: feature-flagged,
    /// typed HttpClient + resilience when enabled, no-op stub when not. The stub does NOT throw —
    /// notifications are fire-and-forget, see <c>NoOpNotificationPublisher</c> for the rationale.
    /// </summary>
    private static void AddNotificationHub(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("NOTIFICATION_HUB_ENABLED", defaultValue: false);
        if (!enabled)
        {
            services.AddSingleton<INotificationPublisher, NoOpNotificationPublisher>();
            return;
        }

        var baseUrl = configuration.GetValue<string>("NOTIFICATION_HUB_URL")
            ?? throw new InvalidOperationException(
                "NOTIFICATION_HUB_URL is required when NOTIFICATION_HUB_ENABLED=true.");

        services
            .AddHttpClient<INotificationPublisher, NotificationHubClient>(client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddResilienceHandler("notification-hub", ConfigureEcosystemResilience);
    }

    /// <summary>
    /// Wires the Workflow Engine client (PRD §5.6d). Identical gating contract to the other ecosystem
    /// services. When disabled, <c>NoOpWorkflowTrigger</c> quietly returns <c>false</c> so call sites
    /// don't need to branch on the flag themselves.
    /// </summary>
    private static void AddWorkflowEngine(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("WORKFLOW_ENGINE_ENABLED", defaultValue: false);
        if (!enabled)
        {
            services.AddSingleton<IWorkflowTrigger, NoOpWorkflowTrigger>();
            return;
        }

        var baseUrl = configuration.GetValue<string>("WORKFLOW_ENGINE_URL")
            ?? throw new InvalidOperationException(
                "WORKFLOW_ENGINE_URL is required when WORKFLOW_ENGINE_ENABLED=true.");

        services
            .AddHttpClient<IWorkflowTrigger, WorkflowEngineClient>(client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddResilienceHandler("workflow-engine", ConfigureEcosystemResilience);
    }

    /// <summary>
    /// Wires the Invoice Reconciliation client (PRD §5.6e). Like the other ecosystem clients, but
    /// calls carry a per-request <c>X-Tenant-API-Key</c> header sourced from the tenant that owns the
    /// obligation — see <c>InvoiceReconClient</c> for the full request shape.
    /// </summary>
    private static void AddInvoiceRecon(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("INVOICE_RECON_ENABLED", defaultValue: false);
        if (!enabled)
        {
            services.AddSingleton<IInvoiceReconClient, NoOpInvoiceReconClient>();
            return;
        }

        var baseUrl = configuration.GetValue<string>("INVOICE_RECON_URL")
            ?? throw new InvalidOperationException(
                "INVOICE_RECON_URL is required when INVOICE_RECON_ENABLED=true.");

        services
            .AddHttpClient<IInvoiceReconClient, InvoiceReconClient>(client =>
            {
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddResilienceHandler("invoice-recon", ConfigureEcosystemResilience);
    }

    /// <summary>
    /// Wires the Compliance Ledger NATS publisher (PRD §5.6c). Swaps HTTP for NATS JetStream so the
    /// resilience pipeline doesn't apply — the NATS client has its own reconnect loop. Singleton
    /// lifetime because the underlying connection is long-lived and expensive to establish.
    /// </summary>
    private static void AddComplianceLedger(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("COMPLIANCE_LEDGER_ENABLED", defaultValue: false);
        if (!enabled)
        {
            services.AddSingleton<IComplianceEventPublisher, NoOpCompliancePublisher>();
            return;
        }

        var natsUrl = configuration.GetValue<string>("NATS_URL")
            ?? throw new InvalidOperationException(
                "NATS_URL is required when COMPLIANCE_LEDGER_ENABLED=true.");

        // Singleton: the connection is expensive to establish and maintains its own reconnect loop.
        // ComplianceLedgerNatsPublisher is IDisposable — the host disposes it during shutdown.
        services.AddSingleton<IComplianceEventPublisher>(sp =>
            new ComplianceLedgerNatsPublisher(
                natsUrl,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ComplianceLedgerNatsPublisher>>()));
    }

    /// <summary>
    /// Wires the inbound Webhook Engine signed-contract downloader (PRD §5.6c). The downloader
    /// always registers (the endpoint handles its own enabled/disabled gate by returning 404 when
    /// the feature flag is off), so the typed <see cref="HttpClient"/> + resilience pipeline is
    /// available any time the Webhook Engine flag is flipped on without a rebuild.
    ///
    /// <para>No base URL — the URL comes from the webhook payload itself (signed and time-limited
    /// by DocuSign / PandaDoc). 60s timeout handles larger signed PDFs while staying well inside
    /// the webhook-engine ack window.</para>
    /// </summary>
    private static void AddWebhookEngine(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddHttpClient<IWebhookDocumentDownloader, WebhookDocumentDownloader>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddResilienceHandler("webhook-downloader", ConfigureEcosystemResilience);
    }

    /// <summary>
    /// Shared resilience configuration for ecosystem HTTP clients — retry 3× exponential 1s → 3s → 9s
    /// + circuit breaker opening after 5 consecutive failures, 30s break. Matches the RAG Platform
    /// policy so operators have a single mental model for every outbound integration.
    /// </summary>
    private static void ConfigureEcosystemResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = false,
        });

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 1.0,
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            SamplingDuration = TimeSpan.FromSeconds(30),
        });
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var fromDatabaseUrl = configuration.GetValue<string>("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(fromDatabaseUrl))
        {
            return fromDatabaseUrl;
        }

        var fromConnectionStrings = configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(fromConnectionStrings))
        {
            return fromConnectionStrings;
        }

        return LocalDevConnectionString;
    }
}
