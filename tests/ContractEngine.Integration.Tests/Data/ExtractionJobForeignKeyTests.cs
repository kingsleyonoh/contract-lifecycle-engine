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
/// Verifies the deferred FK from <c>obligations.extraction_job_id</c> → <c>extraction_jobs(id)</c>
/// now exists after the Batch 020 migration, and that ON DELETE SET NULL is honoured.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ExtractionJobForeignKeyTests
{
    private readonly DatabaseFixture _fixture;

    public ExtractionJobForeignKeyTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Obligation_ExtractionJobId_FK_Exists()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE table_name = 'obligations'
              AND constraint_type = 'FOREIGN KEY'
              AND constraint_name LIKE '%extraction_job%'";
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));

        found.Should().NotBeEmpty(
            because: "obligations.extraction_job_id should have a FK to extraction_jobs(id)");
    }

    [Fact]
    public async Task Obligation_ExtractionJobId_SetNull_OnJobDelete()
    {
        // Create a job, link an obligation to it, delete the job, verify extraction_job_id → NULL.
        var (tenantId, contractId) = await SeedContractAsync();
        var jobId = Guid.NewGuid();

        // Seed via cross-tenant scope so we can insert both.
        using (var cross = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = cross.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.Set<ExtractionJob>().Add(new ExtractionJob
            {
                Id = jobId,
                TenantId = tenantId,
                ContractId = contractId,
                Status = ExtractionStatus.Completed,
                PromptTypes = new[] { "payment" },
                ObligationsFound = 1,
            });
            await crossDb.SaveChangesAsync();
        }

        var obligationId = Guid.NewGuid();
        using (var cross = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = cross.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.Obligations.Add(new Obligation
            {
                Id = obligationId,
                TenantId = tenantId,
                ContractId = contractId,
                ObligationType = ObligationType.Payment,
                Title = "Linked to extraction job",
                DeadlineDate = new DateOnly(2027, 6, 1),
                ExtractionJobId = jobId,
                Source = ObligationSource.RagExtraction,
            });
            await crossDb.SaveChangesAsync();
        }

        // Delete the extraction job via raw SQL — EF doesn't know about the FK relationship from obligation side.
        using (var deleteScope = _fixture.CreateScope())
        {
            var db = deleteScope.ServiceProvider.GetRequiredService<ContractDbContext>();
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM extraction_jobs WHERE id = @id";
            var p = cmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = jobId;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();
        }

        // Verify obligation.extraction_job_id is now NULL.
        using var verifyScope = ScopeFor(tenantId);
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var obligation = await verifyDb.Obligations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == obligationId);
        obligation.Should().NotBeNull();
        obligation!.ExtractionJobId.Should().BeNull(
            because: "ON DELETE SET NULL should have cleared the FK reference");
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
            Name = $"FK-Tenant-{Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_fk",
        };
        var cp = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"FK-CP-{Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = cp.Id,
            Title = "FK test contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(cp);
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
