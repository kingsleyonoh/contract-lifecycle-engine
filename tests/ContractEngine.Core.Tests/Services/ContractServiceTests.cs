using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ContractService"/>. Mocks <see cref="IContractRepository"/>,
/// <see cref="ICounterpartyRepository"/>, and <see cref="ITenantContext"/> via NSubstitute.
/// Verifies tenant tagging, auto-create counterparty, PATCH merge semantics, and the state
/// machine from PRD §4.3.
/// </summary>
public class ContractServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ContractService service, IContractRepository repo, ICounterpartyRepository cpRepo, CounterpartyService cpService, ITenantContext ctx) BuildHarness()
    {
        var repo = Substitute.For<IContractRepository>();
        var cpRepo = Substitute.For<ICounterpartyRepository>();

        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>(TenantA);
        ctx.IsResolved.Returns(true);

        // CounterpartyService is not an interface — use the real one with the substituted repo.
        var cpService = new CounterpartyService(cpRepo, ctx);
        var service = new ContractService(repo, cpRepo, cpService, ctx);
        return (service, repo, cpRepo, cpService, ctx);
    }

    [Fact]
    public async Task CreateAsync_WithExistingCounterpartyId_TagsTenantAndPersistsDraft()
    {
        var (service, repo, cpRepo, _, _) = BuildHarness();
        var cpId = Guid.NewGuid();
        cpRepo.GetByIdAsync(cpId).Returns(new Counterparty { Id = cpId, TenantId = TenantA, Name = "Existing" });

        var request = new CreateContractRequest
        {
            Title = "Master Services Agreement",
            CounterpartyId = cpId,
            ContractType = ContractType.Vendor,
            EffectiveDate = new DateOnly(2026, 5, 1),
            EndDate = new DateOnly(2027, 5, 1),
        };

        var created = await service.CreateAsync(request);

        created.TenantId.Should().Be(TenantA);
        created.CounterpartyId.Should().Be(cpId);
        created.Title.Should().Be("Master Services Agreement");
        created.Status.Should().Be(ContractStatus.Draft);
        created.CurrentVersion.Should().Be(1);
        created.Id.Should().NotBe(Guid.Empty);

        await repo.Received(1).AddAsync(Arg.Is<Contract>(c => c.TenantId == TenantA && c.Status == ContractStatus.Draft));
        await cpRepo.DidNotReceive().AddAsync(Arg.Any<Counterparty>());
    }

    [Fact]
    public async Task CreateAsync_WithNewCounterpartyName_AutoCreatesCounterpartyAndContract()
    {
        var (service, repo, cpRepo, _, _) = BuildHarness();

        var request = new CreateContractRequest
        {
            Title = "Vendor NDA",
            CounterpartyName = "Brand New Counterparty Inc.",
            ContractType = ContractType.Nda,
        };

        var created = await service.CreateAsync(request);

        created.TenantId.Should().Be(TenantA);
        created.CounterpartyId.Should().NotBe(Guid.Empty);

        // Counterparty auto-created with tenant id.
        await cpRepo.Received(1).AddAsync(Arg.Is<Counterparty>(c =>
            c.TenantId == TenantA && c.Name == "Brand New Counterparty Inc."));
        await repo.Received(1).AddAsync(Arg.Any<Contract>());
    }

    [Fact]
    public async Task CreateAsync_WithUnresolvedTenant_Throws()
    {
        var repo = Substitute.For<IContractRepository>();
        var cpRepo = Substitute.For<ICounterpartyRepository>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>((Guid?)null);
        ctx.IsResolved.Returns(false);
        var cpService = new CounterpartyService(cpRepo, ctx);
        var service = new ContractService(repo, cpRepo, cpService, ctx);

        var request = new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
        };

        var act = () => service.CreateAsync(request);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await repo.DidNotReceive().AddAsync(Arg.Any<Contract>());
    }

    [Fact]
    public async Task CreateAsync_WithUnknownCounterpartyId_ThrowsKeyNotFound()
    {
        var (service, _, cpRepo, _, _) = BuildHarness();
        cpRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Counterparty?)null);

        var act = () => service.CreateAsync(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Customer,
        });

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_MergesNonNullFields_AndBumpsUpdatedAt()
    {
        var (service, repo, _, _, _) = BuildHarness();

        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Old Title",
            ContractType = ContractType.Vendor,
            Status = ContractStatus.Draft,
            EffectiveDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2027, 1, 1),
            Currency = "USD",
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-5),
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var updated = await service.UpdateAsync(existing.Id, new UpdateContractRequest
        {
            Title = "New Title",
            Currency = "eur",
            // ReferenceNumber omitted (null) → stays null
        });

        updated.Should().NotBeNull();
        updated!.Title.Should().Be("New Title");
        updated.Currency.Should().Be("EUR");
        updated.ContractType.Should().Be(ContractType.Vendor); // untouched
        updated.Status.Should().Be(ContractStatus.Draft); // not mutated by PATCH
        updated.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        await repo.Received(1).UpdateAsync(Arg.Any<Contract>());
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNull_WhenContractMissing()
    {
        var (service, repo, _, _, _) = BuildHarness();
        repo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var result = await service.UpdateAsync(Guid.NewGuid(), new UpdateContractRequest { Title = "X" });

        result.Should().BeNull();
        await repo.DidNotReceive().UpdateAsync(Arg.Any<Contract>());
    }

    [Fact]
    public async Task ActivateAsync_OnDraftWithValidDates_TransitionsToActive()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Draft,
            EffectiveDate = today.AddDays(-1),
            EndDate = today.AddYears(1),
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.ActivateAsync(existing.Id, null, null);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ContractStatus.Active);
        await repo.Received(1).UpdateAsync(Arg.Any<Contract>());
    }

    [Fact]
    public async Task ActivateAsync_OnTerminatedContract_ThrowsInvalidOperationWithTransitionHint()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Terminated,
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.ActivateAsync(existing.Id, null, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("Terminated");
        ex.Message.Should().Contain("Archived"); // valid next state from Terminated
    }

    [Fact]
    public async Task TerminateAsync_OnDraft_ThrowsInvalidOperation()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Draft,
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.TerminateAsync(existing.Id, "early exit", null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TerminateAsync_OnActiveContract_TransitionsAndStoresReasonInMetadata()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Active,
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.TerminateAsync(existing.Id, "breach of SLA", new DateOnly(2026, 6, 1));

        result.Should().NotBeNull();
        result!.Status.Should().Be(ContractStatus.Terminated);
        result.Metadata.Should().NotBeNull();
        result.Metadata!["termination_reason"].Should().Be("breach of SLA");
        result.Metadata["termination_date"].Should().Be("2026-06-01");
        result.EndDate.Should().Be(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public async Task ArchiveAsync_OnDraft_TransitionsToArchived()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Draft,
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.ArchiveAsync(existing.Id);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ContractStatus.Archived);
    }

    [Fact]
    public async Task ArchiveAsync_OnActiveContract_ThrowsInvalidOperation()
    {
        var (service, repo, _, _, _) = BuildHarness();
        var existing = new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            CounterpartyId = Guid.NewGuid(),
            Title = "Contract",
            Status = ContractStatus.Active,
        };
        repo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.ArchiveAsync(existing.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_DelegatesFiltersToRepository()
    {
        var (service, repo, _, _, _) = BuildHarness();
        repo.ListAsync(Arg.Any<ContractFilters>(), Arg.Any<PageRequest>())
            .Returns(new PagedResult<Contract>(Array.Empty<Contract>(), new PaginationMetadata(null, false, 0)));

        var filters = new ContractFilters { Status = ContractStatus.Active, Type = ContractType.Vendor };
        var page = new PageRequest { PageSize = 10 };
        await service.ListAsync(filters, page);

        await repo.Received(1).ListAsync(filters, page);
    }
}
