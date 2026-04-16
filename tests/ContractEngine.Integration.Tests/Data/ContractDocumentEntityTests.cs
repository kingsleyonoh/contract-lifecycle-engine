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
/// Integration tests for the <c>contract_documents</c> table: round-trip, global tenant filter
/// isolation, and presence of the <c>(tenant_id, contract_id)</c> index from PRD §4.5.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ContractDocumentEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ContractDocumentEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ContractDocument_RoundTripInsertAndRead_PreservesAllFields()
    {
        var (tenantId, contractId) = await SeedContractAsync();

        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var doc = new ContractDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            FileName = "contract.pdf",
            FilePath = $"{tenantId}/{contractId}/contract.pdf",
            FileSizeBytes = 12345,
            MimeType = "application/pdf",
            UploadedBy = "cle_live_xy",
            CreatedAt = DateTime.UtcNow,
        };

        db.ContractDocuments.Add(doc);
        await db.SaveChangesAsync();

        var reloaded = await db.ContractDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doc.Id);
        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.ContractId.Should().Be(contractId);
        reloaded.FileName.Should().Be("contract.pdf");
        reloaded.FilePath.Should().Be(doc.FilePath);
        reloaded.FileSizeBytes.Should().Be(12345);
        reloaded.MimeType.Should().Be("application/pdf");
        reloaded.UploadedBy.Should().Be("cle_live_xy");
    }

    [Fact]
    public async Task ContractDocument_GlobalQueryFilter_HidesOtherTenantsRows()
    {
        var (tenantA, contractA) = await SeedContractAsync();
        var (tenantB, contractB) = await SeedContractAsync();

        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        using (var crossScope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var db = crossScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            db.ContractDocuments.Add(new ContractDocument
            {
                Id = aId,
                TenantId = tenantA,
                ContractId = contractA,
                FileName = "a.pdf",
                FilePath = $"{tenantA}/{contractA}/a.pdf",
                CreatedAt = DateTime.UtcNow,
            });
            db.ContractDocuments.Add(new ContractDocument
            {
                Id = bId,
                TenantId = tenantB,
                ContractId = contractB,
                FileName = "b.pdf",
                FilePath = $"{tenantB}/{contractB}/b.pdf",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scopeA = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantA))))
        {
            var db = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
            var visible = await db.ContractDocuments.AsNoTracking().ToListAsync();
            visible.Should().OnlyContain(d => d.TenantId == tenantA);
            visible.Should().Contain(d => d.Id == aId);
            visible.Should().NotContain(d => d.Id == bId);
        }
    }

    [Fact]
    public async Task ContractDocument_HasIndex_OnTenantIdAndContractId()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename = 'contract_documents'
              AND indexname = 'ix_contract_documents_tenant_id_contract_id'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1, "PRD §4.5 index must exist on the contract_documents table");
    }

    private async Task<(Guid TenantId, Guid ContractId)> SeedContractAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"CD-Tenant {Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_cd",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"CD-CP {Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Seeded Contract",
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
