using FluentAssertions;
using Npgsql;
using Xunit;

namespace ContractEngine.Integration.Tests.SmokeTests;

public class PostgresConnectivityTests
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine;Username=contract_engine;Password=localdev";

    [Fact]
    public async Task CanConnectToLocalPostgresAndSelectOne()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1", connection);
        var result = await cmd.ExecuteScalarAsync();

        result.Should().Be(1);
    }
}
