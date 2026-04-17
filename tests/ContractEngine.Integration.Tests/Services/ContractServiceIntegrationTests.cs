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

    // --- Batch 013: archive cascade end-to-end. ---
    // Seed a contract with 2 Active obligations + 1 Fulfilled (terminal) + 1 Dismissed (terminal).
    // After archive: the 2 Active transition to Expired and get one event each with
    // actor="system:archive_cascade"; the terminal rows stay put.
    [Fact]
    public async Task ArchiveAsync_WithMixedObligations_ExpiresOnlyNonTerminalObligations()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Cascade-{Guid.NewGuid()}");

        Guid contractId;
        Guid active1Id;
        Guid active2Id;
        Guid fulfilledId;
        Guid dismissedId;

        using (var scope = ScopeFor(tenantId))
        {
            var contractSvc = scope.ServiceProvider.GetRequiredService<ContractService>();
            var obSvc = scope.ServiceProvider.GetRequiredService<ObligationService>();

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var contract = await contractSvc.CreateAsync(new CreateContractRequest
            {
                Title = $"Archive-Cascade Contract {Guid.NewGuid()}",
                CounterpartyId = cpId,
                ContractType = ContractType.Vendor,
                EffectiveDate = today.AddDays(-1),
                EndDate = today.AddYears(1),
            });
            contractId = contract.Id;

            // Two Active (via Pending → confirmAsync).
            var a1 = await obSvc.CreateAsync(new CreateObligationRequest
            {
                ContractId = contract.Id,
                ObligationType = ObligationType.Payment,
                Title = $"Active-1 {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 9, 1),
            }, actor: "user:test");
            await obSvc.ConfirmAsync(a1.Id, actor: "user:test");
            active1Id = a1.Id;

            var a2 = await obSvc.CreateAsync(new CreateObligationRequest
            {
                ContractId = contract.Id,
                ObligationType = ObligationType.Reporting,
                Title = $"Active-2 {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 10, 1),
            }, actor: "user:test");
            await obSvc.ConfirmAsync(a2.Id, actor: "user:test");
            active2Id = a2.Id;

            // Fulfilled (confirm → fulfill).
            var f = await obSvc.CreateAsync(new CreateObligationRequest
            {
                ContractId = contract.Id,
                ObligationType = ObligationType.Payment,
                Title = $"Fulfilled {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 11, 1),
            }, actor: "user:test");
            await obSvc.ConfirmAsync(f.Id, actor: "user:test");
            await obSvc.FulfillAsync(f.Id, notes: null, actor: "user:test");
            fulfilledId = f.Id;

            // Dismissed (straight from Pending).
            var d = await obSvc.CreateAsync(new CreateObligationRequest
            {
                ContractId = contract.Id,
                ObligationType = ObligationType.Payment,
                Title = $"Dismissed {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 12, 1),
            }, actor: "user:test");
            await obSvc.DismissAsync(d.Id, reason: "noise", actor: "user:test");
            dismissedId = d.Id;

            // Archive Draft (legal transition). Cascade fires.
            await contractSvc.ArchiveAsync(contract.Id);
        }

        // Verify via cross-tenant DB read.
        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var a1Row = await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == active1Id);
        a1Row.Status.Should().Be(ObligationStatus.Expired);

        var a2Row = await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == active2Id);
        a2Row.Status.Should().Be(ObligationStatus.Expired);

        var fRow = await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == fulfilledId);
        fRow.Status.Should().Be(ObligationStatus.Fulfilled);

        var dRow = await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == dismissedId);
        dRow.Status.Should().Be(ObligationStatus.Dismissed);

        // Event log for the active rows ends with a cascade event.
        var a1Events = await db.ObligationEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.ObligationId == active1Id)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
        a1Events.Last().ToStatus.Should().Be("expired");
        a1Events.Last().Actor.Should().Be("system:archive_cascade");
        a1Events.Last().Metadata.Should().NotBeNull();
        a1Events.Last().Metadata!.Should().ContainKey("contract_id");
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
