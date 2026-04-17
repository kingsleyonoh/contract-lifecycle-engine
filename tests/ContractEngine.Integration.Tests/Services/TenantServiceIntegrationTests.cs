using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// End-to-end integration for <see cref="TenantService"/> against the real Postgres DB.
/// Verifies that <c>RegisterAsync</c> persists a row and that <see cref="ITenantRepository.GetByApiKeyHashAsync"/>
/// retrieves it via the SHA-256 hash of the plaintext key.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TenantServiceIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public TenantServiceIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RegisterAsync_PersistsTenantRow_RetrievableByApiKeyHash()
    {
        using var scope = _fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantService>();
        var repository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var name = $"Integration {Guid.NewGuid()}";
        var result = await service.RegisterAsync(name, "US/Eastern", "USD");

        // Retrieval via repository helper (used by TenantResolutionMiddleware).
        var found = await repository.GetByApiKeyHashAsync(result.Tenant.ApiKeyHash);
        found.Should().NotBeNull();
        found!.Id.Should().Be(result.Tenant.Id);
        found.Name.Should().Be(name);
        found.DefaultTimezone.Should().Be("US/Eastern");
        found.IsActive.Should().BeTrue();

        // Belt-and-braces: confirm the row is actually in the table.
        var row = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == result.Tenant.Id);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByApiKeyHashAsync_ReturnsNull_ForUnknownHash()
    {
        using var scope = _fixture.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        var found = await repository.GetByApiKeyHashAsync($"missing-{Guid.NewGuid():N}");

        found.Should().BeNull();
    }
}
