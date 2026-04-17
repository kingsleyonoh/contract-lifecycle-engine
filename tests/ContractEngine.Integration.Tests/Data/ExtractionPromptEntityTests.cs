using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>extraction_prompts</c> table (PRD §4.11): insert/readback,
/// UNIQUE (tenant_id, prompt_type) with NULLS NOT DISTINCT, system-default rows (tenant_id=null),
/// and tenant-specific rows. ExtractionPrompt does NOT implement ITenantScoped — no global
/// query filter applies.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ExtractionPromptEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ExtractionPromptEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExtractionPrompt_RoundTrip_SystemDefault_PreservesAllFields()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var uniqueType = $"payment-rt-{Guid.NewGuid():N}";
        var prompt = new ExtractionPrompt
        {
            Id = id,
            TenantId = null, // system default
            PromptType = uniqueType,
            PromptText = "Analyze this contract for payment obligations...",
            ResponseSchema = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = "obligation",
            },
            IsActive = true,
        };
        db.Set<ExtractionPrompt>().Add(prompt);
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionPrompt>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().BeNull();
        reloaded.PromptType.Should().Be(uniqueType);
        reloaded.PromptText.Should().Contain("payment obligations");
        reloaded.ResponseSchema.Should().NotBeNull();
        reloaded.ResponseSchema!.Should().ContainKey("type");
        reloaded.IsActive.Should().BeTrue();
        reloaded.CreatedAt.Should().BeAfter(DateTime.MinValue);
        reloaded.UpdatedAt.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task ExtractionPrompt_RoundTrip_TenantSpecific_PreservesAllFields()
    {
        var tenantId = await SeedTenantAsync();
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var prompt = new ExtractionPrompt
        {
            Id = id,
            TenantId = tenantId,
            PromptType = $"custom-{Guid.NewGuid():N}",
            PromptText = "Tenant-specific extraction prompt...",
            IsActive = false,
        };
        db.Set<ExtractionPrompt>().Add(prompt);
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionPrompt>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.IsActive.Should().BeFalse();
        reloaded.ResponseSchema.Should().BeNull();
    }

    [Fact]
    public async Task ExtractionPrompt_UniqueConstraint_RejectsDuplicateTenantPromptType()
    {
        var tenantId = await SeedTenantAsync();
        var uniqueType = $"dup-{Guid.NewGuid():N}";

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        db.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = uniqueType,
            PromptText = "First",
        });
        await db.SaveChangesAsync();

        db.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = uniqueType,
            PromptText = "Second — should fail",
        });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ExtractionPrompt_UniqueConstraint_NullsNotDistinct_RejectsDuplicateSystemDefault()
    {
        // NULLS NOT DISTINCT: two system-default rows (tenant_id=NULL) with the same prompt_type
        // should be rejected — Postgres 15+ UNIQUE behavior.
        var uniqueType = $"nnd-{Guid.NewGuid():N}";

        using var scope1 = _fixture.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<ContractDbContext>();
        db1.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            PromptType = uniqueType,
            PromptText = "System default 1",
        });
        await db1.SaveChangesAsync();

        // Use a fresh scope to avoid change-tracker interference.
        using var scope2 = _fixture.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ContractDbContext>();
        db2.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = null,
            PromptType = uniqueType,
            PromptText = "System default 2 — should fail",
        });
        var act = () => db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ExtractionPrompt_SameTenantDifferentType_Allowed()
    {
        var tenantId = await SeedTenantAsync();

        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        db.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = $"typeA-{Guid.NewGuid():N}",
            PromptText = "Prompt A",
        });
        db.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PromptType = $"typeB-{Guid.NewGuid():N}",
            PromptText = "Prompt B",
        });
        await db.SaveChangesAsync();
        // No exception = pass.
    }

    [Fact]
    public async Task ExtractionPrompt_NoGlobalQueryFilter_VisibleWithoutTenantContext()
    {
        // ExtractionPrompt does NOT implement ITenantScoped, so there is no global query filter.
        // A null-tenant-context scope should still see all rows.
        var id = Guid.NewGuid();
        using var insertScope = _fixture.CreateScope();
        var insertDb = insertScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        insertDb.Set<ExtractionPrompt>().Add(new ExtractionPrompt
        {
            Id = id,
            TenantId = null,
            PromptType = $"nofilter-{Guid.NewGuid():N}",
            PromptText = "Visible without tenant context",
        });
        await insertDb.SaveChangesAsync();

        // Read back from a scope with NullTenantContext (unresolved).
        using var readScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var readDb = readScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var found = await readDb.Set<ExtractionPrompt>()
            .AsNoTracking()
            .AnyAsync(p => p.Id == id);
        found.Should().BeTrue();
    }

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"EP-Tenant-{Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_ep",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}
