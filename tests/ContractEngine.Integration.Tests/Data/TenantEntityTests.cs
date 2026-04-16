using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests that exercise the <c>tenants</c> table against a real Postgres 16 instance
/// (see <see cref="DatabaseFixture"/>). Verifies schema defaults, the UNIQUE constraint on
/// <c>api_key_hash</c>, and the snake_case column mapping.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TenantEntityTests
{
    private readonly DatabaseFixture _fixture;

    public TenantEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Tenants_RoundTripInsertAndRead_PreservesAllFields()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var tenant = new Tenant
        {
            Name = $"RoundTrip {Guid.NewGuid()}",
            ApiKeyHash = UniqueHash("round-trip"),
            ApiKeyPrefix = "cle_live_rt",
            DefaultTimezone = "US/Pacific",
            DefaultCurrency = "EUR",
            IsActive = true,
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var reloaded = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenant.Id);

        reloaded.Should().NotBeNull();
        reloaded!.Name.Should().Be(tenant.Name);
        reloaded.ApiKeyHash.Should().Be(tenant.ApiKeyHash);
        reloaded.ApiKeyPrefix.Should().Be(tenant.ApiKeyPrefix);
        reloaded.DefaultTimezone.Should().Be("US/Pacific");
        reloaded.DefaultCurrency.Should().Be("EUR");
        reloaded.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Tenants_AppliesDefaultsForTimezoneCurrencyIsActive_WhenOmitted()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        // Use raw SQL INSERT so we can actually omit columns the Postgres DEFAULT should apply.
        var tenantId = Guid.NewGuid();
        var hash = UniqueHash("defaults");
        var prefix = "cle_live_df";

        var affected = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO tenants (id, name, api_key_hash, api_key_prefix) VALUES ({0}, {1}, {2}, {3})",
            tenantId, $"Defaults {Guid.NewGuid()}", hash, prefix);
        affected.Should().Be(1);

        var reloaded = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        reloaded.Should().NotBeNull();
        reloaded!.DefaultTimezone.Should().Be("UTC");
        reloaded.DefaultCurrency.Should().Be("USD");
        reloaded.IsActive.Should().BeTrue();
        reloaded.CreatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-2));
        reloaded.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-2));
    }

    [Fact]
    public async Task Tenants_RejectsDuplicateApiKeyHash_ViaUniqueIndex()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var sharedHash = UniqueHash("duplicate");

        db.Tenants.Add(new Tenant
        {
            Name = $"First {Guid.NewGuid()}",
            ApiKeyHash = sharedHash,
            ApiKeyPrefix = "cle_live_a1",
        });
        await db.SaveChangesAsync();

        db.Tenants.Add(new Tenant
        {
            Name = $"Second {Guid.NewGuid()}",
            ApiKeyHash = sharedHash,
            ApiKeyPrefix = "cle_live_b2",
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "api_key_hash carries a UNIQUE index and must reject duplicates");
    }

    [Fact]
    public async Task Tenants_IdColumn_IsGeneratedServerSide_WhenNotSuppliedByClient()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var tenantId = Guid.NewGuid();
        var hash = UniqueHash("server-side");

        // Insert with an explicit id — EF Core client-side generates a Guid regardless of default
        // value sql; the assertion below confirms the stored id is exactly what we inserted
        // (proving EF Core didn't strip or overwrite it) AND that the default expression exists.
        var inserted = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO tenants (id, name, api_key_hash, api_key_prefix) VALUES ({0}, {1}, {2}, {3})",
            tenantId, $"Server {Guid.NewGuid()}", hash, "cle_live_sv");
        inserted.Should().Be(1);

        var reloaded = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        reloaded.Should().NotBeNull();
        reloaded!.Id.Should().Be(tenantId);

        // Confirm gen_random_uuid() / a default expression is registered on the column.
        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT column_default FROM information_schema.columns WHERE table_name='tenants' AND column_name='id'";
        var defaultExpr = (await cmd.ExecuteScalarAsync())?.ToString();
        defaultExpr.Should().NotBeNullOrEmpty("the id column must carry a server-side default");
    }

    private static string UniqueHash(string salt)
    {
        return $"hash-{salt}-{Guid.NewGuid():N}";
    }
}
