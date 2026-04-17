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
/// Unit tests for <see cref="DeadlineAlertService"/>. Mocks the repository and
/// <see cref="ITenantContext"/>. Covers the idempotency contract on
/// <c>CreateIfNotExistsAsync</c>, the acknowledge paths, tenant-guard behaviour, and the
/// bulk-acknowledge filter passthrough.
/// </summary>
public class DeadlineAlertServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (DeadlineAlertService service, IDeadlineAlertRepository repo, ITenantContext ctx)
        BuildHarness(bool tenantResolved = true)
    {
        var repo = Substitute.For<IDeadlineAlertRepository>();
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

        var service = new DeadlineAlertService(repo, ctx);
        return (service, repo, ctx);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_WithNoExistingRow_CallsAddAsync_AndReturnsNewRow()
    {
        var (service, repo, _) = BuildHarness();
        var obligationId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        repo.FindByKeyAsync(obligationId, AlertType.DeadlineApproaching, 30)
            .Returns((DeadlineAlert?)null);

        var result = await service.CreateIfNotExistsAsync(
            obligationId, contractId, AlertType.DeadlineApproaching, 30, "30 days to deadline");

        result.Should().NotBeNull();
        result.TenantId.Should().Be(TenantA);
        result.ObligationId.Should().Be(obligationId);
        result.ContractId.Should().Be(contractId);
        result.AlertType.Should().Be(AlertType.DeadlineApproaching);
        result.DaysRemaining.Should().Be(30);
        result.Acknowledged.Should().BeFalse();
        result.NotificationSent.Should().BeFalse("Batch 015 never flips it; Phase 3 does");
        result.Message.Should().Be("30 days to deadline");

        await repo.Received(1).AddAsync(Arg.Is<DeadlineAlert>(a =>
            a.TenantId == TenantA
            && a.ObligationId == obligationId
            && a.ContractId == contractId
            && a.AlertType == AlertType.DeadlineApproaching
            && a.DaysRemaining == 30));
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_WithExistingRow_ReturnsExisting_AndDoesNotAdd()
    {
        var (service, repo, _) = BuildHarness();
        var obligationId = Guid.NewGuid();
        var existing = new DeadlineAlert
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ObligationId = obligationId,
            ContractId = Guid.NewGuid(),
            AlertType = AlertType.DeadlineApproaching,
            DaysRemaining = 7,
            Message = "already here",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        repo.FindByKeyAsync(obligationId, AlertType.DeadlineApproaching, 7)
            .Returns(existing);

        var result = await service.CreateIfNotExistsAsync(
            obligationId, Guid.NewGuid(), AlertType.DeadlineApproaching, 7, "new message");

        result.Should().BeSameAs(existing);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task CreateIfNotExistsAsync_WithUnresolvedTenant_Throws()
    {
        var (service, repo, _) = BuildHarness(tenantResolved: false);

        var act = () => service.CreateIfNotExistsAsync(
            Guid.NewGuid(), Guid.NewGuid(), AlertType.ObligationOverdue, null, "nope");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task AcknowledgeAsync_OnExisting_SetsAcknowledgedFields_AndReturnsUpdated()
    {
        var (service, repo, _) = BuildHarness();
        var alertId = Guid.NewGuid();
        var existing = new DeadlineAlert
        {
            Id = alertId,
            TenantId = TenantA,
            ObligationId = Guid.NewGuid(),
            ContractId = Guid.NewGuid(),
            AlertType = AlertType.DeadlineApproaching,
            DaysRemaining = 14,
            Message = "ack me",
            Acknowledged = false,
        };
        repo.GetByIdAsync(alertId).Returns(existing);

        var result = await service.AcknowledgeAsync(alertId, "user:alice");

        result.Should().NotBeNull();
        result!.Acknowledged.Should().BeTrue();
        result.AcknowledgedBy.Should().Be("user:alice");
        result.AcknowledgedAt.Should().NotBeNull();
        await repo.Received(1).UpdateAsync(Arg.Is<DeadlineAlert>(a =>
            a.Id == alertId && a.Acknowledged && a.AcknowledgedBy == "user:alice"));
    }

    [Fact]
    public async Task AcknowledgeAsync_OnMissing_ReturnsNull_AndDoesNotUpdate()
    {
        var (service, repo, _) = BuildHarness();
        repo.GetByIdAsync(Arg.Any<Guid>()).Returns((DeadlineAlert?)null);

        var result = await service.AcknowledgeAsync(Guid.NewGuid(), "user:x");

        result.Should().BeNull();
        await repo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task AcknowledgeAsync_WithUnresolvedTenant_Throws()
    {
        var (service, repo, _) = BuildHarness(tenantResolved: false);

        var act = () => service.AcknowledgeAsync(Guid.NewGuid(), "user:x");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await repo.DidNotReceiveWithAnyArgs().GetByIdAsync(default);
        await repo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task AcknowledgeAllAsync_WithNoFilters_DelegatesWithNullArgs()
    {
        var (service, repo, _) = BuildHarness();
        repo.BulkAcknowledgeAsync(TenantA, Arg.Any<string>(), null, null)
            .Returns(5);

        var count = await service.AcknowledgeAllAsync("user:alice");

        count.Should().Be(5);
        await repo.Received(1).BulkAcknowledgeAsync(
            TenantA, "user:alice", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAllAsync_WithContractIdFilter_PassesThrough()
    {
        var (service, repo, _) = BuildHarness();
        var contractId = Guid.NewGuid();
        repo.BulkAcknowledgeAsync(TenantA, Arg.Any<string>(), contractId, null)
            .Returns(2);

        var count = await service.AcknowledgeAllAsync("user:alice", contractId: contractId);

        count.Should().Be(2);
        await repo.Received(1).BulkAcknowledgeAsync(
            TenantA, "user:alice", contractId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAllAsync_WithAlertTypeFilter_PassesThrough()
    {
        var (service, repo, _) = BuildHarness();
        repo.BulkAcknowledgeAsync(TenantA, Arg.Any<string>(), null, AlertType.ObligationOverdue)
            .Returns(3);

        var count = await service.AcknowledgeAllAsync(
            "user:alice", alertType: AlertType.ObligationOverdue);

        count.Should().Be(3);
        await repo.Received(1).BulkAcknowledgeAsync(
            TenantA, "user:alice", null, AlertType.ObligationOverdue, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcknowledgeAllAsync_WithUnresolvedTenant_Throws()
    {
        var (service, repo, _) = BuildHarness(tenantResolved: false);

        var act = () => service.AcknowledgeAllAsync("user:x");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await repo.DidNotReceiveWithAnyArgs()
            .BulkAcknowledgeAsync(default, default!, default, default);
    }

    [Fact]
    public async Task ListAsync_DelegatesToRepository()
    {
        var (service, repo, _) = BuildHarness();
        var filters = new AlertFilters { Acknowledged = false };
        var page = new PageRequest { PageSize = 10 };
        repo.ListAsync(filters, page).Returns(
            new PagedResult<DeadlineAlert>(
                Array.Empty<DeadlineAlert>(), new PaginationMetadata(null, false, 0)));

        await service.ListAsync(filters, page);

        await repo.Received(1).ListAsync(filters, page);
    }

    [Fact]
    public async Task GetByIdAsync_DelegatesToRepository()
    {
        var (service, repo, _) = BuildHarness();
        var id = Guid.NewGuid();
        var row = new DeadlineAlert
        {
            Id = id,
            TenantId = TenantA,
            ObligationId = Guid.NewGuid(),
            ContractId = Guid.NewGuid(),
            AlertType = AlertType.ContractExpiring,
            Message = "!",
        };
        repo.GetByIdAsync(id).Returns(row);

        var found = await service.GetByIdAsync(id);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
    }
}
