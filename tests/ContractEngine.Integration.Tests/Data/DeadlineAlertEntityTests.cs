using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>deadline_alerts</c> table (PRD §4.9): round-trip of all fields,
/// alert_type snake_case persistence, global tenant-filter isolation, presence of the two
/// required indexes, and FK Restrict on obligation-delete.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class DeadlineAlertEntityTests
{
    private readonly DatabaseFixture _fixture;

    public DeadlineAlertEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeadlineAlert_RoundTrip_PreservesAllFields()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var ackTime = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var alert = new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ObligationId = obligationId,
            ContractId = contractId,
            AlertType = AlertType.DeadlineApproaching,
            DaysRemaining = 30,
            Message = "Deadline in 30 business days",
            Acknowledged = true,
            AcknowledgedAt = ackTime,
            AcknowledgedBy = "user:alice",
            NotificationSent = true,
            NotificationSentAt = ackTime.AddMinutes(-5),
            CreatedAt = ackTime.AddDays(-1),
        };
        db.DeadlineAlerts.Add(alert);
        await db.SaveChangesAsync();

        var reloaded = await db.DeadlineAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.ObligationId.Should().Be(obligationId);
        reloaded.ContractId.Should().Be(contractId);
        reloaded.AlertType.Should().Be(AlertType.DeadlineApproaching);
        reloaded.DaysRemaining.Should().Be(30);
        reloaded.Message.Should().Be("Deadline in 30 business days");
        reloaded.Acknowledged.Should().BeTrue();
        reloaded.AcknowledgedBy.Should().Be("user:alice");
        reloaded.NotificationSent.Should().BeTrue();
    }

    [Fact]
    public async Task DeadlineAlert_AlertTypeEnum_PersistsAsSnakeCaseString()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();
        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.DeadlineAlerts.Add(new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ObligationId = obligationId,
            ContractId = contractId,
            AlertType = AlertType.AutoRenewalWarning,
            Message = "autorenewal window",
        });
        await db.SaveChangesAsync();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT alert_type FROM deadline_alerts WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = id;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var found = await reader.ReadAsync();
        found.Should().BeTrue();
        reader.GetString(0).Should().Be("auto_renewal_warning");
    }

    [Fact]
    public async Task DeadlineAlert_GlobalQueryFilter_HidesOtherTenants()
    {
        var (tenantA, contractA, obligationA) = await SeedObligationAsync();
        var (tenantB, contractB, obligationB) = await SeedObligationAsync();

        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.DeadlineAlerts.Add(new DeadlineAlert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ObligationId = obligationA,
                ContractId = contractA,
                AlertType = AlertType.DeadlineApproaching,
                DaysRemaining = 14,
                Message = "Alpha-only alert",
            });
            crossDb.DeadlineAlerts.Add(new DeadlineAlert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ObligationId = obligationB,
                ContractId = contractB,
                AlertType = AlertType.DeadlineApproaching,
                DaysRemaining = 14,
                Message = "Beta-only alert",
            });
            await crossDb.SaveChangesAsync();
        }

        using var scopeA = ScopeFor(tenantA);
        var dbA = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await dbA.DeadlineAlerts.AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(a => a.TenantId == tenantA);
        visible.Should().Contain(a => a.Message == "Alpha-only alert");
        visible.Should().NotContain(a => a.Message == "Beta-only alert");
    }

    [Fact]
    public async Task DeadlineAlert_HasBothRequiredIndexes()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT indexname FROM pg_indexes
            WHERE tablename = 'deadline_alerts'
              AND indexname IN (
                'ix_deadline_alerts_tenant_id_acknowledged_created_at',
                'ix_deadline_alerts_tenant_id_obligation_id')";
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));

        found.Should().BeEquivalentTo(new[]
        {
            "ix_deadline_alerts_tenant_id_acknowledged_created_at",
            "ix_deadline_alerts_tenant_id_obligation_id",
        });
    }

    [Fact]
    public async Task DeadlineAlert_DefaultValues_AppliedOnInsert()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();

        await using var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var id = Guid.NewGuid();
        // Raw insert with only required columns so the DB applies defaults.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = @"
                INSERT INTO deadline_alerts
                    (id, tenant_id, obligation_id, contract_id, alert_type, message)
                VALUES (@id, @tid, @oid, @cid, 'deadline_approaching', 'defaults probe')";
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tid", tenantId);
            insert.Parameters.AddWithValue("oid", obligationId);
            insert.Parameters.AddWithValue("cid", contractId);
            await insert.ExecuteNonQueryAsync();
        }

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var reloaded = await db.DeadlineAlerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.Acknowledged.Should().BeFalse();
        reloaded.NotificationSent.Should().BeFalse();
        reloaded.DaysRemaining.Should().BeNull();
        reloaded.AcknowledgedAt.Should().BeNull();
        reloaded.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task DeadlineAlert_FKRestrict_OnObligationDelete_RaisesError()
    {
        var (tenantId, contractId, obligationId) = await SeedObligationAsync();

        // Seed an alert linked to the obligation.
        using (var seedScope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            seedDb.DeadlineAlerts.Add(new DeadlineAlert
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ObligationId = obligationId,
                ContractId = contractId,
                AlertType = AlertType.DeadlineApproaching,
                DaysRemaining = 7,
                Message = "blocker",
            });
            await seedDb.SaveChangesAsync();
        }

        // Attempt to hard-delete the parent obligation — Restrict should raise.
        await using var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM obligations WHERE id = @id";
        del.Parameters.AddWithValue("id", obligationId);

        Func<Task> act = async () => await del.ExecuteNonQueryAsync();
        await act.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "23503" /* foreign_key_violation */);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ContractId, Guid ObligationId)> SeedObligationAsync()
    {
        using var scope = _fixture.CreateScope(s =>
            s.AddScoped<ITenantContext, NullTenantContext>());
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"DA-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_da",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"DA-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Alerts seed contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "Alerts seed obligation",
            Status = ObligationStatus.Active,
            DeadlineDate = new DateOnly(2026, 12, 1),
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id, obligation.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
