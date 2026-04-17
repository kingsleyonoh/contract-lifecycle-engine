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

        // RAG Platform integration (Batch 019) — feature-flagged per PRD §5.6a.
        // ENABLED=true  → typed HttpClient with retry + circuit breaker resilience pipeline.
        // ENABLED=false → NoOp stub; reads return empty, writes throw (see NoOpRagPlatformClient).
        AddRagPlatformClient(services, configuration);

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
