using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Data;

/// <summary>
/// Integration tests for the <c>contracts</c> table: round-trip, global tenant filter isolation,
/// enum round-trip (stored as snake_case lowercase strings), and presence of the 4 indexes from
/// PRD §4.3.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ContractEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Contract_RoundTripInsertAndRead_PreservesAllFields()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CounterpartyId = cpId,
            Title = $"Test Contract {Guid.NewGuid()}",
            ReferenceNumber = "REF-123",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Draft,
            EffectiveDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2027, 1, 1),
            RenewalNoticeDays = 60,
            AutoRenewal = true,
            AutoRenewalPeriodMonths = 12,
            TotalValue = 150000.50m,
            Currency = "EUR",
            GoverningLaw = "Delaware, US",
            Metadata = new Dictionary<string, object> { { "key", "value" } },
            CurrentVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var reloaded = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contract.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Title.Should().Be(contract.Title);
        reloaded.ReferenceNumber.Should().Be("REF-123");
        reloaded.ContractType.Should().Be(ContractType.Vendor);
        reloaded.Status.Should().Be(ContractStatus.Draft);
        reloaded.EffectiveDate.Should().Be(new DateOnly(2026, 1, 1));
        reloaded.EndDate.Should().Be(new DateOnly(2027, 1, 1));
        reloaded.RenewalNoticeDays.Should().Be(60);
        reloaded.AutoRenewal.Should().BeTrue();
        reloaded.AutoRenewalPeriodMonths.Should().Be(12);
        reloaded.TotalValue.Should().Be(150000.50m);
        reloaded.Currency.Should().Be("EUR");
        reloaded.GoverningLaw.Should().Be("Delaware, US");
        reloaded.CurrentVersion.Should().Be(1);
    }

    [Fact]
    public async Task Contract_GlobalQueryFilter_HidesOtherTenantsRows()
    {
        var (tenantA, cpA) = await SeedTenantAndCounterpartyAsync();
        var (tenantB, cpB) = await SeedTenantAndCounterpartyAsync();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            db.Contracts.Add(new Contract
            {
                Id = aId,
                TenantId = tenantA,
                CounterpartyId = cpA,
                Title = "Contract A",
                ContractType = ContractType.Vendor,
            });
            db.Contracts.Add(new Contract
            {
                Id = bId,
                TenantId = tenantB,
                CounterpartyId = cpB,
                Title = "Contract B",
                ContractType = ContractType.Customer,
            });
            await db.SaveChangesAsync();
        }

        using (var scopeA = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantA))))
        {
            var db = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
            var visible = await db.Contracts.AsNoTracking().ToListAsync();
            visible.Should().OnlyContain(c => c.TenantId == tenantA);
            visible.Should().Contain(c => c.Id == aId);
            visible.Should().NotContain(c => c.Id == bId);
        }

        using (var scopeB = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantB))))
        {
            var db = scopeB.ServiceProvider.GetRequiredService<ContractDbContext>();
            var visible = await db.Contracts.AsNoTracking().ToListAsync();
            visible.Should().OnlyContain(c => c.TenantId == tenantB);
        }
    }

    [Fact]
    public async Task Contract_EnumsPersistAsSnakeCaseStrings()
    {
        var (tenantId, cpId) = await SeedTenantAndCounterpartyAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var id = Guid.NewGuid();
        db.Contracts.Add(new Contract
        {
            Id = id,
            TenantId = tenantId,
            CounterpartyId = cpId,
            Title = "Enum Check",
            ContractType = ContractType.Nda,
            Status = ContractStatus.Active,
        });
        await db.SaveChangesAsync();

        // Read raw VARCHAR values via ADO.NET to verify the column contains "nda"/"active".
        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT contract_type, status FROM contracts WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("nda");
        reader.GetString(1).Should().Be("active");
    }

    [Fact]
    public async Task Contract_HasIndex_OnTenantIdAndStatus()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename = 'contracts'
              AND indexname IN (
                'ix_contracts_tenant_id_status',
                'ix_contracts_tenant_id_counterparty_id',
                'ix_contracts_tenant_id_end_date',
                'ix_contracts_tenant_id_reference_number')";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(4, "all four PRD §4.3 indexes must exist on the contracts table");
    }

    private async Task<(Guid TenantId, Guid CounterpartyId)> SeedTenantAndCounterpartyAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CT-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_ct",
        };
        var cp = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CT-CP {Guid.NewGuid()}",
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(cp);
        await db.SaveChangesAsync();
        return (tenant.Id, cp.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
