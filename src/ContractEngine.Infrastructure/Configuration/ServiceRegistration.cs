using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Repositories;
using ContractEngine.Infrastructure.Storage;
using ContractEngine.Infrastructure.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
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
