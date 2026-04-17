using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <see cref="IExtractionPromptRepository"/>. PRD §4.11 defines the
/// tenant → system-default → null fallback chain for prompt resolution.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ExtractionPromptRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ExtractionPromptRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetPromptAsync_TenantSpecificExists_ReturnsTenantPrompt()
    {
        var tenantId = await SeedTenantAsync();
        var promptType = $"t-{Guid.NewGuid():N}";

        // Seed both a system default and a tenant-specific prompt.
        await SeedPromptAsync(null, promptType, "System default text");
        await SeedPromptAsync(tenantId, promptType, "Tenant-specific text");

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var result = await repo.GetPromptAsync(tenantId, promptType);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(tenantId);
        result.PromptText.Should().Be("Tenant-specific text");
    }

    [Fact]
    public async Task GetPromptAsync_NoTenantSpecific_FallsBackToSystemDefault()
    {
        var tenantId = await SeedTenantAsync();
        var promptType = $"sys-{Guid.NewGuid():N}";

        // Seed only a system default — no tenant-specific.
        await SeedPromptAsync(null, promptType, "System fallback text");

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var result = await repo.GetPromptAsync(tenantId, promptType);

        result.Should().NotBeNull();
        result!.TenantId.Should().BeNull();
        result.PromptText.Should().Be("System fallback text");
    }

    [Fact]
    public async Task GetPromptAsync_NeitherExists_ReturnsNull()
    {
        var tenantId = await SeedTenantAsync();

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var result = await repo.GetPromptAsync(tenantId, "nonexistent-type-xyz");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPromptAsync_InactivePromptSkipped()
    {
        var tenantId = await SeedTenantAsync();
        var promptType = $"inactive-{Guid.NewGuid():N}";

        // Seed an inactive tenant prompt — should fall back to system default.
        using var seedScope = _fixture.CreateScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        seedDb.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = promptType,
            PromptText = "Inactive — should not be returned",
            IsActive = false,
        });
        seedDb.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            PromptType = promptType,
            PromptText = "Active system default",
            IsActive = true,
        });
        await seedDb.SaveChangesAsync();

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var result = await repo.GetPromptAsync(tenantId, promptType);

        result.Should().NotBeNull();
        result!.TenantId.Should().BeNull();
        result.PromptText.Should().Be("Active system default");
    }

    [Fact]
    public async Task AddAsync_PersistsPrompt()
    {
        var tenantId = await SeedTenantAsync();
        var promptType = $"add-{Guid.NewGuid():N}";

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var prompt = new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = promptType,
            PromptText = "Test add",
        };
        await repo.AddAsync(prompt);

        var readBack = await repo.GetPromptAsync(tenantId, promptType);
        readBack.Should().NotBeNull();
        readBack!.PromptText.Should().Be("Test add");
    }

    [Fact]
    public async Task ListSystemDefaultsAsync_ReturnsOnlyNullTenantRows()
    {
        var uniqueType = $"lsd-{Guid.NewGuid():N}";
        await SeedPromptAsync(null, uniqueType, "System default for list test");

        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionPromptRepository>();

        var defaults = await repo.ListSystemDefaultsAsync();

        defaults.Should().Contain(p => p.PromptType == uniqueType);
        defaults.Should().OnlyContain(p => p.TenantId == null);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"EPR-Tenant-{Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_er",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task SeedPromptAsync(Guid? tenantId, string promptType, string promptText)
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        db.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = promptType,
            PromptText = promptText,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }
}
