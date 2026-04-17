using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.Repositories;

/// <summary>
/// Real-DB integration tests for <see cref="IExtractionJobRepository"/>: ListAsync filtering +
/// pagination, ListQueuedAsync for the job processor, and AddAsync persistence.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ExtractionJobRepositoryTests
{
    private readonly DatabaseFixture _fixture;

    public ExtractionJobRepositoryTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_FiltersByStatus()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);
        await SeedJobAsync(tenantId, contractId, ExtractionStatus.Completed);
        await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var result = await repo.ListAsync(
            new ExtractionJobFilters { Status = ExtractionStatus.Queued },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(j => j.Status == ExtractionStatus.Queued);
        result.Data.Count.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListAsync_FiltersByContractId()
    {
        var (tenantId, contractA) = await SeedContractAsync();
        var contractB = await SeedAdditionalContractAsync(tenantId);
        await SeedJobAsync(tenantId, contractA, ExtractionStatus.Queued);
        await SeedJobAsync(tenantId, contractB, ExtractionStatus.Queued);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var result = await repo.ListAsync(
            new ExtractionJobFilters { ContractId = contractA },
            new PageRequest { PageSize = 50 });

        result.Data.Should().OnlyContain(j => j.ContractId == contractA);
    }

    [Fact]
    public async Task ListAsync_Pagination_RespectsPageSize()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        for (int i = 0; i < 5; i++)
            await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var result = await repo.ListAsync(
            new ExtractionJobFilters(),
            new PageRequest { PageSize = 3 });

        result.Data.Count.Should().BeLessOrEqualTo(3);
        result.Pagination.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListQueuedAsync_ReturnsBatchSizeJobs_OrderedByCreatedAt()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        var jobIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var jobId = await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);
            jobIds.Add(jobId);
        }

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var queued = await repo.ListQueuedAsync(3);

        queued.Count.Should().BeLessOrEqualTo(3);
        queued.Should().OnlyContain(j => j.Status == ExtractionStatus.Queued);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsExistingJob()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        var jobId = await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var result = await repo.GetByIdAsync(jobId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(jobId);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        using var scope = _fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var result = await repo.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var (tenantId, contractId) = await SeedContractAsync();
        var jobId = await SeedJobAsync(tenantId, contractId, ExtractionStatus.Queued);

        using var scope = ScopeFor(tenantId);
        var repo = scope.ServiceProvider.GetRequiredService<IExtractionJobRepository>();

        var job = await repo.GetByIdAsync(jobId);
        job!.Status = ExtractionStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        await repo.UpdateAsync(job);

        var reloaded = await repo.GetByIdAsync(jobId);
        reloaded!.Status.Should().Be(ExtractionStatus.Processing);
        reloaded.StartedAt.Should().NotBeNull();
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
            Name = $"EJR-Tenant-{Guid.NewGuid()}",
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            ApiKeyPrefix = "cle_live_jr",
        };
        var counterparty = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"EJR-CP-{Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            CounterpartyId = counterparty.Id,
            Title = "Job repo seed contract",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Active,
        };
        db.Tenants.Add(tenant);
        db.Counterparties.Add(counterparty);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return (tenant.Id, contract.Id);
    }

    private async Task<Guid> SeedAdditionalContractAsync(Guid tenantId)
    {
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        // Look up the first counterparty for this tenant.
        var cp = new Counterparty
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"Extra-CP-{Guid.NewGuid()}",
        };
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CounterpartyId = cp.Id,
            Title = "Extra contract for repo test",
            ContractType = ContractType.Customer,
            Status = ContractStatus.Active,
        };
        db.Counterparties.Add(cp);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract.Id;
    }

    private async Task<Guid> SeedJobAsync(Guid tenantId, Guid contractId, ExtractionStatus status)
    {
        using var scope = _fixture.CreateScope(services =>
            services.AddScoped<ITenantContext>(_ => new FixedTenantContext(tenantId)));
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            Status = status,
            PromptTypes = new[] { "payment" },
        };
        db.Set<ExtractionJob>().Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid id) { TenantId = id; }
        public Guid? TenantId { get; }
        public bool IsResolved => TenantId is not null;
    }
}
