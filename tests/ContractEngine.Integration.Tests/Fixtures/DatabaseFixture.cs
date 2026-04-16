using ContractEngine.Core.Abstractions;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace ContractEngine.Integration.Tests.Fixtures;

/// <summary>
/// Shared fixture for integration tests that need a live <see cref="ContractDbContext"/>.
/// Creates a dedicated <c>contract_engine_test</c> database (if missing), applies all EF Core
/// migrations once per test-run, and exposes a <see cref="CreateScope"/> helper that each test
/// uses to resolve fresh DI instances.
///
/// Tests share the same database; they MUST use unique GUIDs / API keys to avoid collisions.
/// This is simpler and faster than per-test transactions with rollback, and is safe so long as
/// every insert uses a random Guid/api_key.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    private const string MaintenanceConnectionString =
        "Host=localhost;Port=5445;Database=postgres;Username=contract_engine;Password=localdev";

    public const string TestDatabaseName = "contract_engine_test";

    public string ConnectionString { get; } =
        $"Host=localhost;Port=5445;Database={TestDatabaseName};Username=contract_engine;Password=localdev";

    public ServiceProvider Provider { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await EnsureTestDatabaseExistsAsync();
        BuildProvider();
        await ApplyMigrationsAsync();
        await SeedHolidayCalendarsAsync();
    }

    public Task DisposeAsync()
    {
        Provider?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a DI scope wired for the test database. Callers override <c>ITenantContext</c>
    /// via <paramref name="configure"/> when they need a specific tenant resolved (or
    /// cross-tenant access via <see cref="NullTenantContext"/>).
    /// </summary>
    public IServiceScope CreateScope(Action<IServiceCollection>? configure = null)
    {
        if (configure is null)
        {
            return Provider.CreateScope();
        }

        // Build a child container that layers the caller's overrides on top of the base provider.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfiguration());
        services.AddContractEngineInfrastructure(BuildConfiguration());
        configure(services);
        var child = services.BuildServiceProvider();
        return child.CreateScope();
    }

    private void BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfiguration());
        services.AddContractEngineInfrastructure(BuildConfiguration());
        Provider = services.BuildServiceProvider();
    }

    private IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            { "DATABASE_URL", ConnectionString },
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task ApplyMigrationsAsync()
    {
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        await db.Database.MigrateAsync();
    }

    private async Task SeedHolidayCalendarsAsync()
    {
        // Idempotent: re-runs per fixture initialisation safely skip already-inserted rows.
        using var scope = Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        await HolidayCalendarSeeder.SeedAsync(db);
    }

    private static async Task EnsureTestDatabaseExistsAsync()
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();

        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        exists.Parameters.AddWithValue("name", TestDatabaseName);
        var found = await exists.ExecuteScalarAsync();

        if (found is null)
        {
            await using var create = connection.CreateCommand();
            // No parameters — identifier cannot be parameterised. We control the literal.
            create.CommandText = $"CREATE DATABASE {TestDatabaseName}";
            await create.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition(nameof(DatabaseCollection))]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // Marker class — xUnit collects here via attribute discovery.
}
