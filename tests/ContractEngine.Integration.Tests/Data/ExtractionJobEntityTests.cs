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
/// Integration tests for the <c>extraction_jobs</c> table (PRD §4.8): TEXT[] round-trip for
/// prompt_types, JSONB raw_responses, ExtractionStatus enum persistence, global tenant query
/// filter, indexes, and FK to contracts/documents.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ExtractionJobEntityTests
{
    private readonly DatabaseFixture _fixture;

    public ExtractionJobEntityTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExtractionJob_RoundTrip_PreservesAllFields()
    {
        var (tenantId, contractId, documentId) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var job = new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            DocumentId = documentId,
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment", "renewal", "compliance" },
            ObligationsFound = 0,
            ObligationsConfirmed = 0,
            ErrorMessage = null,
            RagDocumentId = "rag-doc-123",
            RawResponses = new Dictionary<string, object>
            {
                ["payment"] = "raw response data",
            },
            RetryCount = 0,
        };
        db.Set<ExtractionJob>().Add(job);
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id);

        reloaded.Should().NotBeNull();
        reloaded!.TenantId.Should().Be(tenantId);
        reloaded.ContractId.Should().Be(contractId);
        reloaded.DocumentId.Should().Be(documentId);
        reloaded.Status.Should().Be(ExtractionStatus.Queued);
        reloaded.PromptTypes.Should().BeEquivalentTo(new[] { "payment", "renewal", "compliance" });
        reloaded.ObligationsFound.Should().Be(0);
        reloaded.ObligationsConfirmed.Should().Be(0);
        reloaded.ErrorMessage.Should().BeNull();
        reloaded.RagDocumentId.Should().Be("rag-doc-123");
        reloaded.RawResponses.Should().NotBeNull();
        reloaded.RawResponses!.Should().ContainKey("payment");
        reloaded.RetryCount.Should().Be(0);
        reloaded.StartedAt.Should().BeNull();
        reloaded.CompletedAt.Should().BeNull();
        reloaded.CreatedAt.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task ExtractionJob_StatusEnum_PersistsAsSnakeCaseString()
    {
        var (tenantId, contractId, _) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Set<ExtractionJob>().Add(new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            Status = ExtractionStatus.Processing,
            PromptTypes = new[] { "payment" },
        });
        await db.SaveChangesAsync();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM extraction_jobs WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = id;
        cmd.Parameters.Add(p);

        var rawValue = (string?)await cmd.ExecuteScalarAsync();
        rawValue.Should().Be("processing");
    }

    [Fact]
    public async Task ExtractionJob_TextArray_PromptTypes_RoundTrips()
    {
        var (tenantId, contractId, _) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        var types = new[] { "payment", "renewal", "compliance", "performance" };
        db.Set<ExtractionJob>().Add(new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            Status = ExtractionStatus.Queued,
            PromptTypes = types,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id);

        reloaded!.PromptTypes.Should().BeEquivalentTo(types);
    }

    [Fact]
    public async Task ExtractionJob_GlobalQueryFilter_HidesOtherTenants()
    {
        var (tenantA, contractA, _) = await SeedContractWithDocumentAsync();
        var (tenantB, contractB, _) = await SeedContractWithDocumentAsync();

        // Seed jobs for both tenants using cross-tenant scope.
        using (var cross = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext, NullTenantContext>()))
        {
            var crossDb = cross.ServiceProvider.GetRequiredService<ContractDbContext>();
            crossDb.Set<ExtractionJob>().Add(new ExtractionJob
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                ContractId = contractA,
                Status = ExtractionStatus.Queued,
                PromptTypes = new[] { "payment" },
            });
            crossDb.Set<ExtractionJob>().Add(new ExtractionJob
            {
                Id = Guid.NewGuid(),
                TenantId = tenantB,
                ContractId = contractB,
                Status = ExtractionStatus.Queued,
                PromptTypes = new[] { "renewal" },
            });
            await crossDb.SaveChangesAsync();
        }

        // Tenant A scope sees only its jobs.
        using var scopeA = ScopeFor(tenantA);
        var dbA = scopeA.ServiceProvider.GetRequiredService<ContractDbContext>();
        var visible = await dbA.Set<ExtractionJob>().AsNoTracking().ToListAsync();
        visible.Should().OnlyContain(j => j.TenantId == tenantA);
    }

    [Fact]
    public async Task ExtractionJob_HasRequiredIndexes()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT indexname FROM pg_indexes
            WHERE tablename = 'extraction_jobs'
              AND indexname IN (
                'ix_extraction_jobs_tenant_id_status',
                'ix_extraction_jobs_tenant_id_contract_id')";
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));

        found.Should().BeEquivalentTo(new[]
        {
            "ix_extraction_jobs_tenant_id_status",
            "ix_extraction_jobs_tenant_id_contract_id",
        });
    }

    [Fact]
    public async Task ExtractionJob_NullableDocument_AcceptsNull()
    {
        var (tenantId, contractId, _) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Set<ExtractionJob>().Add(new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            DocumentId = null, // optional
            Status = ExtractionStatus.Queued,
            PromptTypes = new[] { "payment" },
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id);
        reloaded!.DocumentId.Should().BeNull();
    }

    [Fact]
    public async Task ExtractionJob_CompletedStatus_WithTimestamps()
    {
        var (tenantId, contractId, _) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.Set<ExtractionJob>().Add(new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            Status = ExtractionStatus.Completed,
            PromptTypes = new[] { "payment", "renewal" },
            ObligationsFound = 5,
            ObligationsConfirmed = 3,
            StartedAt = now.AddMinutes(-2),
            CompletedAt = now,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id);
        reloaded!.Status.Should().Be(ExtractionStatus.Completed);
        reloaded.ObligationsFound.Should().Be(5);
        reloaded.ObligationsConfirmed.Should().Be(3);
        reloaded.StartedAt.Should().NotBeNull();
        reloaded.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ExtractionJob_FailedStatus_WithErrorMessage()
    {
        var (tenantId, contractId, _) = await SeedContractWithDocumentAsync();

        using var scope = ScopeFor(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var id = Guid.NewGuid();
        db.Set<ExtractionJob>().Add(new ExtractionJob
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            Status = ExtractionStatus.Failed,
            PromptTypes = new[] { "payment" },
            ErrorMessage = "RAG Platform unavailable",
            RetryCount = 3,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id);
        reloaded!.Status.Should().Be(ExtractionStatus.Failed);
        reloaded.ErrorMessage.Should().Be("RAG Platform unavailable");
        reloaded.RetryCount.Should().Be(3);
    }

    private IServiceScope ScopeFor(Guid tenantId) =>
        _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));

    private async Task<(Guid TenantId, Guid ContractId, Guid DocumentId)> SeedContractWithDocumentAsync()
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"EJ-Tenant-{Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_ej",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"EJ-CP-{Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Extraction seed contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        var document = new ContractDocument
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            ContractId = contract.Id,
            FileName = "test.pdf",
            FilePath = "test/path/test.pdf",
            FileSizeBytes = 1024,
            MimeType = "application/pdf",
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        db.ContractDocuments.Add(document);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id, document.Id);
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
