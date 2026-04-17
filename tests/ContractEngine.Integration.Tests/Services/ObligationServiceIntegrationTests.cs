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
/// End-to-end integration tests for <see cref="ObligationService"/> against the real Postgres
/// test database. Exercises the event-sourcing contract (create writes NO event; confirm /
/// dismiss each write exactly one event) and the detail-with-events read path.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ObligationServiceIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public ObligationServiceIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_PersistsPendingObligation_AndWritesNoEvent()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Obligation-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<ObligationService>();

        var created = await service.CreateAsync(new CreateObligationRequest
        {
            ContractId = contractId,
            ObligationType = ObligationType.Payment,
            Title = $"Integration obligation {Guid.NewGuid()}",
            DeadlineDate = new DateOnly(2026, 6, 1),
        }, actor: "user:test");

        created.Status.Should().Be(ObligationStatus.Pending);
        created.Source.Should().Be(ObligationSource.Manual);

        // Verify DB state using a cross-tenant context so nothing is hidden.
        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var row = await db.Obligations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == created.Id);
        row.Should().NotBeNull();
        row!.Status.Should().Be(ObligationStatus.Pending);
        row.TenantId.Should().Be(tenantId);

        // No event row — creation never emits an event.
        var eventCount = await db.ObligationEvents
            .IgnoreQueryFilters()
            .CountAsync(e => e.ObligationId == created.Id);
        eventCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmAsync_OnPending_UpdatesToActive_AndWritesOneEvent()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Confirm-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        Guid obligationId;
        using (var scope = ScopeFor(tenantId))
        {
            var service = scope.ServiceProvider.GetRequiredService<ObligationService>();
            var created = await service.CreateAsync(new CreateObligationRequest
            {
                ContractId = contractId,
                ObligationType = ObligationType.Payment,
                Title = $"Confirmable obligation {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 7, 1),
            }, actor: "user:test");
            obligationId = created.Id;

            var confirmed = await service.ConfirmAsync(obligationId, actor: "user:alice");
            confirmed.Should().NotBeNull();
            confirmed!.Status.Should().Be(ObligationStatus.Active);
        }

        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var row = await db.Obligations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == obligationId);
        row.Should().NotBeNull();
        row!.Status.Should().Be(ObligationStatus.Active);

        var events = await db.ObligationEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.ObligationId == obligationId)
            .ToListAsync();
        events.Should().HaveCount(1);
        events[0].FromStatus.Should().Be("pending");
        events[0].ToStatus.Should().Be("active");
        events[0].Actor.Should().Be("user:alice");
        events[0].Reason.Should().NotBeNull();
    }

    [Fact]
    public async Task DismissAsync_OnPending_UpdatesToDismissed_AndCapturesReason()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Dismiss-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        Guid obligationId;
        using (var scope = ScopeFor(tenantId))
        {
            var service = scope.ServiceProvider.GetRequiredService<ObligationService>();
            var created = await service.CreateAsync(new CreateObligationRequest
            {
                ContractId = contractId,
                ObligationType = ObligationType.Reporting,
                Title = $"Dismissable obligation {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 8, 1),
            }, actor: "user:test");
            obligationId = created.Id;

            var dismissed = await service.DismissAsync(
                obligationId, reason: "extractor duplicate", actor: "user:bob");
            dismissed.Should().NotBeNull();
            dismissed!.Status.Should().Be(ObligationStatus.Dismissed);
        }

        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        var row = await db.Obligations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == obligationId);
        row!.Status.Should().Be(ObligationStatus.Dismissed);

        var events = await db.ObligationEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.ObligationId == obligationId)
            .ToListAsync();
        events.Should().HaveCount(1);
        events[0].FromStatus.Should().Be("pending");
        events[0].ToStatus.Should().Be("dismissed");
        events[0].Actor.Should().Be("user:bob");
        events[0].Reason.Should().Be("extractor duplicate");
    }

    // --- Batch 013: recurring fulfil cascade + archive bulk-expire round-trip. ---
    [Fact]
    public async Task FulfillAsync_OnRecurringMonthly_SpawnsActiveFollowUpInDb()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Recur-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        Guid parentId;
        using (var scope = ScopeFor(tenantId))
        {
            var service = scope.ServiceProvider.GetRequiredService<ObligationService>();
            var parent = await service.CreateAsync(new CreateObligationRequest
            {
                ContractId = contractId,
                ObligationType = ObligationType.Payment,
                Title = $"Monthly integration {Guid.NewGuid()}",
                DeadlineDate = new DateOnly(2026, 4, 16),
                Recurrence = ObligationRecurrence.Monthly,
                Amount = 1500m,
                Currency = "USD",
            }, actor: "user:test");
            parentId = parent.Id;

            await service.ConfirmAsync(parentId, actor: "user:test");
            var fulfilled = await service.FulfillAsync(parentId, notes: "paid via ACH", actor: "user:test");
            fulfilled!.Status.Should().Be(ObligationStatus.Fulfilled);
        }

        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        // Parent persisted as Fulfilled.
        var parentRow = await db.Obligations.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(o => o.Id == parentId);
        parentRow.Status.Should().Be(ObligationStatus.Fulfilled);

        // Exactly one sibling row on the same contract → Active with next_due_date + 1 month.
        var siblings = await db.Obligations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.ContractId == contractId && o.Id != parentId)
            .ToListAsync();
        siblings.Should().HaveCount(1);
        siblings[0].Status.Should().Be(ObligationStatus.Active);
        siblings[0].NextDueDate.Should().Be(new DateOnly(2026, 5, 16));
        siblings[0].Recurrence.Should().Be(ObligationRecurrence.Monthly);
        siblings[0].Amount.Should().Be(1500m);

        // Event log for the parent: confirm + fulfill. For the child: one provenance event.
        var parentEvents = await db.ObligationEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.ObligationId == parentId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync();
        parentEvents.Should().HaveCount(2);
        parentEvents[1].ToStatus.Should().Be("fulfilled");

        var childEvents = await db.ObligationEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.ObligationId == siblings[0].Id)
            .ToListAsync();
        childEvents.Should().HaveCount(1);
        childEvents[0].ToStatus.Should().Be("active");
        childEvents[0].Actor.Should().Be("system");
        childEvents[0].Reason.Should().NotBeNull();
        childEvents[0].Reason!.Should().Contain("recurring");
    }

    [Fact]
    public async Task ExpireDueToContractArchiveAsync_ExpiresOnlyNonTerminalRows()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Bulk-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        Guid active1, active2, active3, fulfilled, dismissed;
        using (var scope = ScopeFor(tenantId))
        {
            var service = scope.ServiceProvider.GetRequiredService<ObligationService>();
            active1 = (await SeedAndConfirmAsync(service, contractId)).Id;
            active2 = (await SeedAndConfirmAsync(service, contractId)).Id;
            active3 = (await SeedAndConfirmAsync(service, contractId)).Id;

            var ff = await service.CreateAsync(CreateRequest(contractId), actor: "user:test");
            await service.ConfirmAsync(ff.Id, actor: "user:test");
            await service.FulfillAsync(ff.Id, notes: null, actor: "user:test");
            fulfilled = ff.Id;

            var d = await service.CreateAsync(CreateRequest(contractId), actor: "user:test");
            await service.DismissAsync(d.Id, reason: "extractor noise", actor: "user:test");
            dismissed = d.Id;

            await service.ExpireDueToContractArchiveAsync(contractId, actor: "system:archive_cascade");
        }

        using var verify = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = verify.ServiceProvider.GetRequiredService<ContractDbContext>();

        (await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == active1))
            .Status.Should().Be(ObligationStatus.Expired);
        (await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == active2))
            .Status.Should().Be(ObligationStatus.Expired);
        (await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == active3))
            .Status.Should().Be(ObligationStatus.Expired);
        (await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == fulfilled))
            .Status.Should().Be(ObligationStatus.Fulfilled);
        (await db.Obligations.IgnoreQueryFilters().AsNoTracking().FirstAsync(o => o.Id == dismissed))
            .Status.Should().Be(ObligationStatus.Dismissed);

        // Three cascade events — one per active row.
        var cascadeEvents = await db.ObligationEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Actor == "system:archive_cascade" && e.ToStatus == "expired")
            .ToListAsync();
        cascadeEvents.Count.Should().BeGreaterOrEqualTo(3);
    }

    private static CreateObligationRequest CreateRequest(Guid contractId) => new()
    {
        ContractId = contractId,
        ObligationType = ObligationType.Payment,
        Title = $"Ob {Guid.NewGuid()}",
        DeadlineDate = new DateOnly(2026, 6, 1),
    };

    private static async Task<Obligation> SeedAndConfirmAsync(ObligationService service, Guid contractId)
    {
        var ob = await service.CreateAsync(CreateRequest(contractId), actor: "user:test");
        await service.ConfirmAsync(ob.Id, actor: "user:test");
        return ob;
    }

    [Fact]
    public async Task GetByIdWithEventsAsync_ReturnsObligationAndChronologicalEvents()
    {
        var tenantId = await SeedTenantAsync();
        var cpId = await SeedCounterpartyAsync(tenantId, $"Timeline-CP-{Guid.NewGuid()}");
        var contractId = await SeedContractAsync(tenantId, cpId);

        using var scope = ScopeFor(tenantId);
        var service = scope.ServiceProvider.GetRequiredService<ObligationService>();

        var created = await service.CreateAsync(new CreateObligationRequest
        {
            ContractId = contractId,
            ObligationType = ObligationType.Payment,
            Title = $"Timeline obligation {Guid.NewGuid()}",
            DeadlineDate = new DateOnly(2026, 9, 1),
        }, actor: "user:test");

        // On fresh creation: no events yet.
        var freshResult = await service.GetByIdWithEventsAsync(created.Id);
        freshResult.Should().NotBeNull();
        freshResult!.Value.Events.Should().BeEmpty();

        // Confirm → one event.
        await service.ConfirmAsync(created.Id, actor: "user:alice");

        var confirmedResult = await service.GetByIdWithEventsAsync(created.Id);
        confirmedResult.Should().NotBeNull();
        confirmedResult!.Value.Obligation.Status.Should().Be(ObligationStatus.Active);
        confirmedResult.Value.Events.Should().HaveCount(1);
        confirmedResult.Value.Events[0].FromStatus.Should().Be("pending");
        confirmedResult.Value.Events[0].ToStatus.Should().Be("active");
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
            Name = $"Obligation-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_ob",
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

    private async Task<Guid> SeedContractAsync(Guid tenantId, Guid counterpartyId)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Contracts.Add(new Contract
        {
            Id = id,
            TenantId = tenantId,
            CounterpartyId = counterpartyId,
            Title = $"Contract {Guid.NewGuid()}",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
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
