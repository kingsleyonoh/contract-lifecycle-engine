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
/// Integration tests for the <c>obligations</c> table (PRD §4.6): JSONB metadata round-trip,
/// enum snake_case persistence for all five obligation enums, global tenant-filter isolation,
/// and presence of the four required composite indexes.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ObligationEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ObligationEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Obligation_RoundTrip_PreservesAllFields()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var obligation = new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = ObligationType.TerminationNotice,
            Status = ObligationStatus.Pending,
            Title = "Provide 90-day termination notice",
            Description = "Written notice to counterparty",
            ResponsibleParty = ResponsibleParty.Us,
            DeadlineDate = new DateOnly(2027, 1, 1),
            DeadlineFormula = "contract.end_date - 90d",
            Recurrence = ObligationRecurrence.Annually,
            NextDueDate = new DateOnly(2027, 1, 1),
            Amount = 12345.67m,
            Currency = "EUR",
            AlertWindowDays = 45,
            GracePeriodDays = 7,
            BusinessDayCalendar = "DE",
            Source = ObligationSource.RagExtraction,
            ExtractionJobId = Guid.NewGuid(),
            ConfidenceScore = 0.92m,
            ClauseReference = "§12.3",
            Metadata = new Dictionary<string, object>
            {
                ["origin"] = "contract v2",
                ["priority"] = "high",
            },
        };
        db.Obligations.Add(obligation);
        await db.SaveChangesAsync();

        var reloaded = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.ContractId.Should().Be(contractId);
        reloaded.ObligationType.Should().Be(ObligationType.TerminationNotice);
        reloaded.Status.Should().Be(ObligationStatus.Pending);
        reloaded.Title.Should().Be("Provide 90-day termination notice");
        reloaded.Description.Should().Be("Written notice to counterparty");
        reloaded.ResponsibleParty.Should().Be(ResponsibleParty.Us);
        reloaded.DeadlineDate.Should().Be(new DateOnly(2027, 1, 1));
        reloaded.DeadlineFormula.Should().Be("contract.end_date - 90d");
        reloaded.Recurrence.Should().Be(ObligationRecurrence.Annually);
        reloaded.NextDueDate.Should().Be(new DateOnly(2027, 1, 1));
        reloaded.Amount.Should().Be(12345.67m);
        reloaded.Currency.Should().Be("EUR");
        reloaded.AlertWindowDays.Should().Be(45);
        reloaded.GracePeriodDays.Should().Be(7);
        reloaded.BusinessDayCalendar.Should().Be("DE");
        reloaded.Source.Should().Be(ObligationSource.RagExtraction);
        reloaded.ExtractionJobId.Should().Be(obligation.ExtractionJobId);
        reloaded.ConfidenceScore.Should().Be(0.92m);
        reloaded.ClauseReference.Should().Be("§12.3");
        reloaded.Metadata.Should().NotBeNull();
        reloaded.Metadata!.Should().ContainKey("origin");
        reloaded.Metadata.Should().ContainKey("priority");
    }

    [Fact]
    public async Task Obligation_EnumsPersistAsSnakeCaseStrings()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Obligations.Add(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = ObligationType.TerminationNotice,
            Status = ObligationStatus.Upcoming,
            Title = "Enum check",
            ResponsibleParty = ResponsibleParty.Counterparty,
            Recurrence = ObligationRecurrence.Quarterly,
            Source = ObligationSource.RagExtraction,
            DeadlineDate = new DateOnly(2026, 12, 31),
        });
        await db.SaveChangesAsync();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT obligation_type, status, responsible_party, recurrence, source
            FROM obligations WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = id;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        var found = await reader.ReadAsync();
        found.Should().BeTrue();
        reader.GetString(0).Should().Be("termination_notice");
        reader.GetString(1).Should().Be("upcoming");
        reader.GetString(2).Should().Be("counterparty");
        reader.GetString(3).Should().Be("quarterly");
        reader.GetString(4).Should().Be("rag_extraction");
    }

    [Fact]
    public async Task Obligation_GlobalQueryFilter_HidesOtherTenants()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();

        // Seed a row for each tenant via a cross-tenant scope so both save.
        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.Obligations.Add(new Obligation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ContractId = contractA,
                ObligationType = ObligationType.Payment,
                Title = "Alpha-only obligation",
                DeadlineDate = new DateOnly(2026, 9, 1),
            });
            crossDb.Obligations.Add(new Obligation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ContractId = contractB,
                ObligationType = ObligationType.Payment,
                Title = "Beta-only obligation",
                DeadlineDate = new DateOnly(2026, 9, 1),
            });
            await crossDb.SaveChangesAsync();
        }

        // Tenant A scope sees only its row.
        using var scopeA = ScopeFor(tenantA);
        var dbA = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await dbA.Obligations.AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(o => o.TenantId == tenantA);
        visible.Should().Contain(o => o.Title == "Alpha-only obligation");
        visible.Should().NotContain(o => o.Title == "Beta-only obligation");
    }

    [Fact]
    public async Task Obligation_HasAllFourRequiredIndexes()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT indexname FROM pg_indexes
            WHERE tablename = 'obligations'
              AND indexname IN (
                'ix_obligations_tenant_id_status',
                'ix_obligations_tenant_id_contract_id',
                'ix_obligations_tenant_id_next_due_date',
                'ix_obligations_tenant_id_obligation_type')";
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));

        found.Should().BeEquivalentTo(new[]
        {
            "ix_obligations_tenant_id_status",
            "ix_obligations_tenant_id_contract_id",
            "ix_obligations_tenant_id_next_due_date",
            "ix_obligations_tenant_id_obligation_type",
        });
    }

    [Fact]
    public async Task Obligation_NullableFields_AcceptNull()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Obligations.Add(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = ObligationType.Other,
            Title = "Minimal obligation",
            // All nullable fields left null on purpose — schema permits.
            DeadlineFormula = "contract.start + 30d",
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.DeadlineDate.Should().BeNull();
        reloaded.NextDueDate.Should().BeNull();
        reloaded.Amount.Should().BeNull();
        reloaded.Recurrence.Should().BeNull();
        reloaded.ExtractionJobId.Should().BeNull();
        reloaded.ConfidenceScore.Should().BeNull();
        reloaded.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task Obligation_DefaultValues_AppliedOnInsert()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        await using var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var id = Guid.NewGuid();
        // Insert via raw SQL with *only* the required fields — lets the DB apply server-side defaults.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = @"
                INSERT INTO obligations (id, tenant_id, contract_id, obligation_type, title, deadline_date)
                VALUES (@id, @tid, @cid, 'payment', 'Defaults probe', '2026-09-01')";
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tid", tenantId);
            insert.Parameters.AddWithValue("cid", contractId);
            await insert.ExecuteNonQueryAsync();
        }

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var reloaded = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(ObligationStatus.Pending);
        reloaded.ResponsibleParty.Should().Be(ResponsibleParty.Us);
        reloaded.Currency.Should().Be("USD");
        reloaded.AlertWindowDays.Should().Be(30);
        reloaded.GracePeriodDays.Should().Be(0);
        reloaded.BusinessDayCalendar.Should().Be("US");
        reloaded.Source.Should().Be(ObligationSource.Manual);
    }

    [Fact]
    public async Task Obligation_DecimalPrecision_RoundTripsAmountAndConfidence()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Obligations.Add(new Obligation
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationType = ObligationType.Payment,
            Title = "Decimal probe",
            DeadlineDate = new DateOnly(2026, 10, 15),
            Amount = 999999999.99m,
            ConfidenceScore = 0.85m,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        reloaded!.Amount.Should().Be(999999999.99m);
        reloaded.ConfidenceScore.Should().Be(0.85m);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"Obl-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_ob",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"Obl-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Obligation seed contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
