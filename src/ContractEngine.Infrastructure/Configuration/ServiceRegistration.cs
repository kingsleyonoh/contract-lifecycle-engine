using ContractEngine.Core.Abstractions;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContractEngine.Infrastructure.Configuration;

/// <summary>
/// Extension methods for registering the Infrastructure layer (EF Core DbContext, repositories,
/// external clients) into the ASP.NET Core DI container.
/// </summary>
public static class ServiceRegistration
{
    private const string LocalDevConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine;Username=contract_engine;Password=localdev";

    /// <summary>
    /// Registers the Contract Engine infrastructure services: <see cref="ContractDbContext"/>
    /// (scoped) and <see cref="ITenantContext"/> (scoped). Until
    /// <c>TenantResolutionMiddleware</c> ships, the tenant context defaults to
    /// <see cref="NullTenantContext"/> — callers that require a resolved tenant must check
    /// <see cref="ITenantContext.IsResolved"/>.
    ///
    /// Reads the Postgres connection string from the <c>DATABASE_URL</c> environment/config
    /// key first, then <c>ConnectionStrings:Default</c>, then falls back to a documented local
    /// development string (Docker Postgres on host port 5445).
    /// </summary>
    public static IServiceCollection AddContractEngineInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);

        services.AddDbContext<ContractDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITenantContext, NullTenantContext>();

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
