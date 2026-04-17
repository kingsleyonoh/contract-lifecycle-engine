using ContractEngine.Core.Abstractions;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

public class ContractDbContextTests
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine;Username=contract_engine;Password=localdev";

    private static ServiceProvider BuildProvider()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "DATABASE_URL", connectionString },
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddContractEngineInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ContractDbContext_CanBeResolvedFromDi()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetService<ContractDbContext>();

        db.Should().NotBeNull("AddContractEngineInfrastructure must register ContractDbContext");
    }

    [Fact]
    public void ITenantContext_IsRegistered_AsUnresolvedAccessorByDefault()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // Batch 004 replaced NullTenantContext with TenantContextAccessor. Before the middleware
        // resolves a tenant, the accessor reports the same "unresolved" state as NullTenantContext.
        tenantContext.IsResolved.Should().BeFalse();
        tenantContext.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task ContractDbContext_CanConnectToLivePostgres()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var canConnect = await db.Database.CanConnectAsync();

        canConnect.Should().BeTrue("the integration test target Postgres must be reachable on 5445");
    }

    [Fact]
    public async Task ContractDbContext_CanExecuteRawSelectOne()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = await command.ExecuteScalarAsync();

        result.Should().Be(1);
    }
}
