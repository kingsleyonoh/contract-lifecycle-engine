using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Services;

/// <summary>
/// Round-trip integration test for the Contract lifecycle state machine against the real Postgres
/// DB. Verifies that each transition (Draft → Active → Terminated → Archived) persists correctly
/// and that the global tenant query filter keeps mutations scoped to the resolved tenant.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractServiceIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public ContractServiceIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FullLifecycle_DraftToActiveToTerminatedToArchived_PersistsAllTransitions()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Lifecycle-{Guid.NewGuid()}");

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<ContractService>();

        // Create draft contract.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var draft = await service.CreateAsync(new CreateContractRequest
        {
            Title = $"Lifecycle Contract {Guid.NewGuid()}",
            CounterpartyId = cpId,
            ContractType = ContractType.Vendor,
            EffectiveDate = today.AddDays(-1),
            EndDate = today.AddYears(1),
        });
        draft.Status.Should().Be(ContractStatus.Draft);

        // Activate.
        var activated = await service.ActivateAsync(draft.Id, null, null);
        activated.Should().NotBeNull();
        activated!.Status.Should().Be(ContractStatus.Active);

        // Terminate.
        var terminated = await service.TerminateAsync(draft.Id, "mutual agreement", today);
        terminated.Should().NotBeNull();
        terminated!.Status.Should().Be(ContractStatus.Terminated);
        terminated.Metadata.Should().ContainKey("termination_reason");

        // Archive.
        var archived = await service.ArchiveAsync(draft.Id);
        archived.Should().NotBeNull();
        archived!.Status.Should().Be(ContractStatus.Archived);

        // Assert terminal state persisted in DB. Use IgnoreQueryFilters since NullTenantContext
        // would otherwise hide the row (TenantId == null makes the filter fail closed).
        using var verifyScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verifyScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var row = await db.Contracts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == draft.Id);
        row.Should().NotBeNull();
        row!.Status.Should().Be(ContractStatus.Archived);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<Guid> SeedTenantAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Lifecycle-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_lc",
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedCounterpartyAsync(Guid tenantId, string name)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Counterparties.Add(new Counterparty
        {
            Id = id,
            TenantId = tenantId,
            Name = name,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
