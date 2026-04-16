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
/// Unit tests for <see cref="ContractVersionService"/>. Asserts the invariants that matter: new
/// rows carry a monotonically-increasing <c>VersionNumber</c>, <c>Contract.CurrentVersion</c> is
/// kept in sync, and cross-tenant / auth sad paths respect the PRD §5.1 contract.
/// </summary>
public class ContractVersionServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ContractVersionService service,
             IContractVersionRepository versionRepo,
             IContractRepository contractRepo,
             ITenantContext ctx) BuildHarness()
    {
        var versionRepo = Substitute.For<IContractVersionRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>(TenantA);
        ctx.IsResolved.Returns(true);

        var service = new ContractVersionService(versionRepo, contractRepo, ctx);
        return (service, versionRepo, contractRepo, ctx);
    }

    [Fact]
    public async Task CreateAsync_OnFreshContract_StartsAtVersionTwo()
    {
        // Fresh contracts seed with CurrentVersion=1; first POST should yield version_number=2.
        var (service, versionRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        var contract = new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
            CurrentVersion = 1,
        };
        contractRepo.GetByIdAsync(contractId).Returns(contract);
        versionRepo.GetNextVersionNumberAsync(contractId).Returns(1);

        var version = await service.CreateAsync(contractId, "first amendment", null, "alice@example.com");

        version.VersionNumber.Should().Be(2);
        version.TenantId.Should().Be(TenantA);
        version.ContractId.Should().Be(contractId);
        version.ChangeSummary.Should().Be("first amendment");
        version.CreatedBy.Should().Be("alice@example.com");
        version.DiffResult.Should().BeNull();
        contract.CurrentVersion.Should().Be(2, "contract row must track latest version");

        await versionRepo.Received(1).AddAsync(Arg.Any<ContractVersion>(), Arg.Any<CancellationToken>());
        await contractRepo.Received(1).UpdateAsync(contract, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WhenRowsAlreadyExist_IncrementsPastHighest()
    {
        var (service, versionRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
            CurrentVersion = 4,
        });
        versionRepo.GetNextVersionNumberAsync(contractId).Returns(5);

        var version = await service.CreateAsync(contractId, "renewal", new DateOnly(2026, 10, 1), null);

        version.VersionNumber.Should().Be(5);
        version.EffectiveDate.Should().Be(new DateOnly(2026, 10, 1));
        version.CreatedBy.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_OnMissingContract_ThrowsKeyNotFound()
    {
        var (service, versionRepo, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var act = () => service.CreateAsync(Guid.NewGuid(), "anything", null, null);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await versionRepo.DidNotReceive().AddAsync(Arg.Any<ContractVersion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithUnresolvedTenant_ThrowsUnauthorized()
    {
        var versionRepo = Substitute.For<IContractVersionRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>((Guid?)null);
        ctx.IsResolved.Returns(false);
        var service = new ContractVersionService(versionRepo, contractRepo, ctx);

        var act = () => service.CreateAsync(Guid.NewGuid(), "x", null, null);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ListByContractAsync_ForMissingContract_ReturnsEmptyPage()
    {
        var (service, versionRepo, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var page = await service.ListByContractAsync(Guid.NewGuid(), new PageRequest());

        page.Data.Should().BeEmpty();
        page.Pagination.TotalCount.Should().Be(0);
        await versionRepo.DidNotReceive().ListByContractAsync(
            Arg.Any<Guid>(), Arg.Any<PageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListByContractAsync_ForExistingContract_DelegatesToRepo()
    {
        var (service, versionRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        versionRepo.ListByContractAsync(contractId, Arg.Any<PageRequest>())
            .Returns(new PagedResult<ContractVersion>(
                Array.Empty<ContractVersion>(),
                new PaginationMetadata(null, false, 0)));

        var page = await service.ListByContractAsync(contractId, new PageRequest { PageSize = 10 });

        page.Should().NotBeNull();
        await versionRepo.Received(1).ListByContractAsync(
            contractId, Arg.Any<PageRequest>(), Arg.Any<CancellationToken>());
    }
}
