using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ContractTagService"/>. Mocks the repositories and tenant context so
/// the service's normalisation + orchestration contract is exercised in isolation.
/// </summary>
public class ContractTagServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ContractTagService service,
             IContractTagRepository tagRepo,
             IContractRepository contractRepo,
             ITenantContext ctx) BuildHarness()
    {
        var tagRepo = Substitute.For<IContractTagRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>(TenantA);
        ctx.IsResolved.Returns(true);

        var service = new ContractTagService(tagRepo, contractRepo, ctx);
        return (service, tagRepo, contractRepo, ctx);
    }

    [Fact]
    public async Task ReplaceTagsAsync_OnExistingContract_NormalisesAndCallsRepoOnce()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        tagRepo.ReplaceTagsAsync(TenantA, contractId, Arg.Any<IReadOnlyList<string>>())
            .Returns(ci => ((IReadOnlyList<string>)ci[2])
                .Select(t => new ContractTag { TenantId = TenantA, ContractId = contractId, Tag = t })
                .ToList());

        var result = await service.ReplaceTagsAsync(contractId, new[] { "  vendor  ", "high-value", "vendor" });

        result.Should().HaveCount(2, "duplicates after trim must collapse to a single entry");
        result.Select(t => t.Tag).Should().ContainInOrder("vendor", "high-value");
        await tagRepo.Received(1).ReplaceTagsAsync(
            TenantA,
            contractId,
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceTagsAsync_WithEmptyList_IsIdempotentClear()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        tagRepo.ReplaceTagsAsync(TenantA, contractId, Arg.Any<IReadOnlyList<string>>())
            .Returns(Array.Empty<ContractTag>());

        var result = await service.ReplaceTagsAsync(contractId, Array.Empty<string>());

        result.Should().BeEmpty();
        await tagRepo.Received(1).ReplaceTagsAsync(
            TenantA,
            contractId,
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceTagsAsync_WithEmptyString_ThrowsArgumentException()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });

        var act = () => service.ReplaceTagsAsync(contractId, new[] { "valid", "" });

        await act.Should().ThrowAsync<ArgumentException>();
        await tagRepo.DidNotReceive().ReplaceTagsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceTagsAsync_WithTagOver100Chars_ThrowsArgumentException()
    {
        var (service, _, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });

        var overLong = new string('a', ContractTagService.MaxTagLength + 1);
        var act = () => service.ReplaceTagsAsync(contractId, new[] { overLong });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReplaceTagsAsync_PreservesCaseSensitivity()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        contractRepo.GetByIdAsync(contractId).Returns(new Contract
        {
            Id = contractId,
            TenantId = TenantA,
            Status = ContractStatus.Active,
        });
        tagRepo.ReplaceTagsAsync(TenantA, contractId, Arg.Any<IReadOnlyList<string>>())
            .Returns(ci => ((IReadOnlyList<string>)ci[2])
                .Select(t => new ContractTag { TenantId = TenantA, ContractId = contractId, Tag = t })
                .ToList());

        var result = await service.ReplaceTagsAsync(contractId, new[] { "Vendor", "vendor" });

        result.Should().HaveCount(2, "PRD §4.12 treats 'Vendor' and 'vendor' as distinct tags");
    }

    [Fact]
    public async Task ReplaceTagsAsync_OnMissingContract_ThrowsKeyNotFound()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var act = () => service.ReplaceTagsAsync(Guid.NewGuid(), new[] { "x" });

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await tagRepo.DidNotReceive().ReplaceTagsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceTagsAsync_WithUnresolvedTenant_ThrowsUnauthorized()
    {
        var tagRepo = Substitute.For<IContractTagRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
        var ctx = Substitute.For<ITenantContext>();
        ctx.TenantId.Returns<Guid?>((Guid?)null);
        ctx.IsResolved.Returns(false);
        var service = new ContractTagService(tagRepo, contractRepo, ctx);

        var act = () => service.ReplaceTagsAsync(Guid.NewGuid(), new[] { "x" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ListByContractAsync_ForMissingContract_ReturnsEmptyList()
    {
        var (service, tagRepo, contractRepo, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var result = await service.ListByContractAsync(Guid.NewGuid());

        result.Should().BeEmpty();
        await tagRepo.DidNotReceive().ListByContractAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
