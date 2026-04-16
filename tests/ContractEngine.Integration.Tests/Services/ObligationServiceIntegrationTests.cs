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
