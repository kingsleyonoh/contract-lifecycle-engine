using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ConflictDetectionService"/>. Mocks IRagPlatformClient,
/// IContractRepository, IDeadlineAlertRepository, ITenantContext.
/// Covers PRD §5.5 cross-contract conflict detection.
/// </summary>
public class ConflictDetectionServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ConflictDetectionService service,
             IRagPlatformClient ragClient,
             IContractRepository contractRepo,
             IDeadlineAlertRepository alertRepo,
             ITenantContext ctx) BuildHarness(bool tenantResolved = true)
    {
        var ragClient = Substitute.For<IRagPlatformClient>();
        var contractRepo = Substitute.For<IContractRepository>();
        var alertRepo = Substitute.For<IDeadlineAlertRepository>();
        var ctx = Substitute.For<ITenantContext>();
        if (tenantResolved)
        {
            ctx.TenantId.Returns<Guid?>(TenantA);
            ctx.IsResolved.Returns(true);
        }
        else
        {
            ctx.TenantId.Returns<Guid?>((Guid?)null);
            ctx.IsResolved.Returns(false);
        }

        var service = new ConflictDetectionService(ragClient, contractRepo, alertRepo, ctx);
        return (service, ragClient, contractRepo, alertRepo, ctx);
    }

    [Fact]
    public async Task DetectConflictsAsync_WithActiveContractsSameCounterparty_CallsRagAndCreatesAlerts()
    {
        var (service, ragClient, contractRepo, alertRepo, _) = BuildHarness();
        var counterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var otherContractId = Guid.NewGuid();

        var target = MakeContract(contractId, counterpartyId, ContractStatus.Active);
        contractRepo.GetByIdAsync(contractId, Arg.Any<CancellationToken>()).Returns(target);

        var other = MakeContract(otherContractId, counterpartyId, ContractStatus.Active);
        contractRepo.ListAsync(
                Arg.Any<ContractFilters>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Contract>(
                new[] { target, other },
                new PaginationMetadata(null, false, 2)));

        ragClient.ChatSyncAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new RagChatResult(
                """{"conflicts":[{"description":"overlapping exclusivity clause"}]}""",
                Array.Empty<RagChatSource>()));

        var conflicts = await service.DetectConflictsAsync(contractId);

        conflicts.Should().NotBeEmpty();
        await ragClient.Received().ChatSyncAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectConflictsAsync_NoOtherContracts_ReturnsEmpty()
    {
        var (service, ragClient, contractRepo, alertRepo, _) = BuildHarness();
        var counterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        var target = MakeContract(contractId, counterpartyId, ContractStatus.Active);
        contractRepo.GetByIdAsync(contractId, Arg.Any<CancellationToken>()).Returns(target);

        // Only the target contract returned — no others.
        contractRepo.ListAsync(
                Arg.Any<ContractFilters>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Contract>(
                new[] { target },
                new PaginationMetadata(null, false, 1)));

        var conflicts = await service.DetectConflictsAsync(contractId);

        conflicts.Should().BeEmpty();
        await ragClient.DidNotReceiveWithAnyArgs().ChatSyncAsync(
            default!, default, default, default);
    }

    [Fact]
    public async Task DetectConflictsAsync_RagDisabled_SkipsSilentlyAndReturnsEmpty()
    {
        var (service, ragClient, contractRepo, _, _) = BuildHarness();
        var counterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var otherContractId = Guid.NewGuid();

        var target = MakeContract(contractId, counterpartyId, ContractStatus.Active);
        contractRepo.GetByIdAsync(contractId, Arg.Any<CancellationToken>()).Returns(target);

        var other = MakeContract(otherContractId, counterpartyId, ContractStatus.Active);
        contractRepo.ListAsync(
                Arg.Any<ContractFilters>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Contract>(
                new[] { target, other },
                new PaginationMetadata(null, false, 2)));

        ragClient.ChatSyncAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RagPlatformException("RAG Platform is disabled (NoOp stub)"));

        var conflicts = await service.DetectConflictsAsync(contractId);

        conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectConflictsAsync_MissingContract_ThrowsKeyNotFound()
    {
        var (service, _, contractRepo, _, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Contract?)null);

        var act = () => service.DetectConflictsAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static Contract MakeContract(Guid id, Guid counterpartyId, ContractStatus status) => new()
    {
        Id = id,
        TenantId = TenantA,
        CounterpartyId = counterpartyId,
        Title = $"Contract {id}",
        ContractType = ContractType.Vendor,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
