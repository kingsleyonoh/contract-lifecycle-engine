using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ObligationService"/>. Mocks the repositories and
/// <see cref="ITenantContext"/>; uses the REAL <see cref="ObligationStateMachine"/> (stateless,
/// no external deps, always in scope). Covers PRD §5.3:
/// <list type="bullet">
///   <item>CreateAsync persists a Pending manual obligation and does NOT write an event on
///     creation (events fire only on transitions).</item>
///   <item>ConfirmAsync / DismissAsync — pending-state transitions each write exactly one event.</item>
///   <item>Invalid transitions bubble as <see cref="ObligationTransitionException"/>.</item>
///   <item>GetByIdWithEventsAsync returns obligation + events in ascending <c>created_at</c> order.</item>
///   <item>Unresolved tenant → <see cref="UnauthorizedAccessException"/>; missing contract →
///     <see cref="KeyNotFoundException"/>.</item>
/// </list>
/// </summary>
public class ObligationServiceTests
{
    private static readonly Guid TenantA = Guid.NewGuid();

    private (ObligationService service,
             IObligationRepository obligationRepo,
             IObligationEventRepository eventRepo,
             IContractRepository contractRepo,
             ITenantContext ctx,
             ObligationStateMachine stateMachine) BuildHarness(bool tenantResolved = true)
    {
        var obligationRepo = Substitute.For<IObligationRepository>();
        var eventRepo = Substitute.For<IObligationEventRepository>();
        var contractRepo = Substitute.For<IContractRepository>();
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

        var stateMachine = new ObligationStateMachine();
        var service = new ObligationService(obligationRepo, eventRepo, contractRepo, stateMachine, ctx);
        return (service, obligationRepo, eventRepo, contractRepo, ctx, stateMachine);
    }

    private static Contract ContractFixture(Guid? id = null, Guid? tenantId = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TenantId = tenantId ?? TenantA,
        CounterpartyId = Guid.NewGuid(),
        Title = "Contract",
        ContractType = ContractType.Vendor,
        Status = ContractStatus.Active,
    };

    [Fact]
    public async Task CreateAsync_WithValidRequest_PersistsPendingObligation_AndDoesNotWriteEvent()
    {
        var (service, obligationRepo, eventRepo, contractRepo, _, _) = BuildHarness();
        var contract = ContractFixture();
        contractRepo.GetByIdAsync(contract.Id).Returns(contract);

        var request = new CreateObligationRequest
        {
            ContractId = contract.Id,
            ObligationType = ObligationType.Payment,
            Title = "Monthly license fee",
            DeadlineDate = new DateOnly(2026, 6, 1),
        };

        var created = await service.CreateAsync(request, actor: "user:test");

        created.TenantId.Should().Be(TenantA);
        created.ContractId.Should().Be(contract.Id);
        created.Status.Should().Be(ObligationStatus.Pending);
        created.Source.Should().Be(ObligationSource.Manual);
        created.Title.Should().Be("Monthly license fee");
        created.Id.Should().NotBe(Guid.Empty);

        await obligationRepo.Received(1).AddAsync(Arg.Is<Obligation>(o =>
            o.TenantId == TenantA &&
            o.Status == ObligationStatus.Pending &&
            o.Source == ObligationSource.Manual));
        // No event on creation — events fire only on transitions.
        await eventRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task CreateAsync_WithUnresolvedTenant_ThrowsUnauthorized()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness(tenantResolved: false);

        var request = new CreateObligationRequest
        {
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "X",
            DeadlineDate = new DateOnly(2026, 6, 1),
        };

        var act = () => service.CreateAsync(request, actor: "user:test");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await obligationRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task CreateAsync_WithNonexistentContract_ThrowsKeyNotFound()
    {
        var (service, obligationRepo, _, contractRepo, _, _) = BuildHarness();
        contractRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Contract?)null);

        var request = new CreateObligationRequest
        {
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "X",
            DeadlineDate = new DateOnly(2026, 6, 1),
        };

        var act = () => service.CreateAsync(request, actor: "user:test");
        await act.Should().ThrowAsync<KeyNotFoundException>();
        await obligationRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task ConfirmAsync_OnPending_PersistsActive_AndWritesEvent()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Pending → Active",
            Status = ObligationStatus.Pending,
            DeadlineDate = new DateOnly(2026, 6, 1),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var confirmed = await service.ConfirmAsync(existing.Id, actor: "user:alice");

        confirmed.Should().NotBeNull();
        confirmed!.Status.Should().Be(ObligationStatus.Active);

        await obligationRepo.Received(1).UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == existing.Id && o.Status == ObligationStatus.Active));
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.ObligationId == existing.Id
            && e.TenantId == TenantA
            && e.FromStatus == "pending"
            && e.ToStatus == "active"
            && e.Actor == "user:alice"
            && e.Reason != null
            && e.Reason!.Contains("confirm")));
    }

    [Fact]
    public async Task ConfirmAsync_OnActive_ThrowsObligationTransitionException()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Already active",
            Status = ObligationStatus.Active,
            DeadlineDate = new DateOnly(2026, 6, 1),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.ConfirmAsync(existing.Id, actor: "user:alice");

        var ex = (await act.Should().ThrowAsync<ObligationTransitionException>()).Which;
        ex.CurrentStatus.Should().Be(ObligationStatus.Active);
        ex.RequestedStatus.Should().Be(ObligationStatus.Active);
        // No persistence should have occurred on a rejected transition.
        await obligationRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        await eventRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task ConfirmAsync_WithMissingObligation_ReturnsNull()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        obligationRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Obligation?)null);

        var result = await service.ConfirmAsync(Guid.NewGuid(), actor: "user:alice");
        result.Should().BeNull();
    }

    [Fact]
    public async Task DismissAsync_OnPending_PersistsDismissed_AndWritesEvent()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Dismiss me",
            Status = ObligationStatus.Pending,
            DeadlineDate = new DateOnly(2026, 6, 1),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var dismissed = await service.DismissAsync(existing.Id, reason: "noise from extractor", actor: "user:bob");

        dismissed.Should().NotBeNull();
        dismissed!.Status.Should().Be(ObligationStatus.Dismissed);

        await obligationRepo.Received(1).UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == existing.Id && o.Status == ObligationStatus.Dismissed));
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.FromStatus == "pending"
            && e.ToStatus == "dismissed"
            && e.Actor == "user:bob"
            && e.Reason == "noise from extractor"));
    }

    [Fact]
    public async Task DismissAsync_OnActive_ThrowsObligationTransitionException()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Cannot dismiss active",
            Status = ObligationStatus.Active,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.DismissAsync(existing.Id, reason: null, actor: "user:bob");

        (await act.Should().ThrowAsync<ObligationTransitionException>()).Which
            .CurrentStatus.Should().Be(ObligationStatus.Active);
        await obligationRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        await eventRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task DismissAsync_WithMissingObligation_ReturnsNull()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        obligationRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Obligation?)null);

        var result = await service.DismissAsync(Guid.NewGuid(), reason: "gone", actor: "user:x");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdWithEventsAsync_ReturnsObligationAndEventsInCreatedAtAscendingOrder()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var obligationId = Guid.NewGuid();
        var existing = new Obligation
        {
            Id = obligationId,
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Event history",
            Status = ObligationStatus.Active,
        };
        obligationRepo.GetByIdAsync(obligationId).Returns(existing);

        var older = new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ObligationId = obligationId,
            FromStatus = "pending",
            ToStatus = "active",
            Actor = "user:alice",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        var newer = new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ObligationId = obligationId,
            FromStatus = "active",
            ToStatus = "upcoming",
            Actor = "system",
            CreatedAt = DateTime.UtcNow,
        };
        eventRepo.ListAllByObligationAscAsync(obligationId, Arg.Any<CancellationToken>())
            .Returns(new List<ObligationEvent> { older, newer });

        var result = await service.GetByIdWithEventsAsync(obligationId);

        result.Should().NotBeNull();
        result!.Value.Obligation.Id.Should().Be(obligationId);
        result.Value.Events.Should().HaveCount(2);
        result.Value.Events[0].Id.Should().Be(older.Id);
        result.Value.Events[1].Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task GetByIdWithEventsAsync_WithMissingObligation_ReturnsNull()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        obligationRepo.GetByIdAsync(Arg.Any<Guid>()).Returns((Obligation?)null);

        var result = await service.GetByIdWithEventsAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_DelegatesToRepository()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        obligationRepo.ListAsync(Arg.Any<ObligationFilters>(), Arg.Any<PageRequest>())
            .Returns(new PagedResult<Obligation>(Array.Empty<Obligation>(), new PaginationMetadata(null, false, 0)));

        var filters = new ObligationFilters { Status = ObligationStatus.Pending };
        var page = new PageRequest { PageSize = 10 };
        await service.ListAsync(filters, page);

        await obligationRepo.Received(1).ListAsync(filters, page);
    }

    [Fact]
    public async Task GetByIdAsync_DelegatesToRepository()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var id = Guid.NewGuid();
        var row = new Obligation
        {
            Id = id,
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Single",
            Status = ObligationStatus.Pending,
        };
        obligationRepo.GetByIdAsync(id).Returns(row);

        var found = await service.GetByIdAsync(id);
        found.Should().NotBeNull();
        found!.Id.Should().Be(id);
    }

    // --- Batch 013: fulfill / waive / dispute / resolve-dispute / expire-cascade / events list ---

    [Fact]
    public async Task FulfillAsync_OnActive_WithOneTimeRecurrence_TransitionsAndDoesNotSpawn()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "One-time fee",
            Status = ObligationStatus.Active,
            Recurrence = ObligationRecurrence.OneTime,
            NextDueDate = new DateOnly(2026, 5, 10),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.FulfillAsync(existing.Id, notes: "paid in full", actor: "user:alice");

        result.Should().NotBeNull();
        result!.Status.Should().Be(ObligationStatus.Fulfilled);

        // Exactly one Update + one Add event (no spawn for OneTime).
        await obligationRepo.Received(1).UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == existing.Id && o.Status == ObligationStatus.Fulfilled));
        await obligationRepo.DidNotReceive().AddAsync(Arg.Any<Obligation>());
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.ObligationId == existing.Id && e.FromStatus == "active" && e.ToStatus == "fulfilled"));
    }

    [Fact]
    public async Task FulfillAsync_OnPending_ThrowsObligationTransitionException()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Cannot fulfill pending",
            Status = ObligationStatus.Pending,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.FulfillAsync(existing.Id, notes: null, actor: "user:alice");

        (await act.Should().ThrowAsync<ObligationTransitionException>()).Which
            .CurrentStatus.Should().Be(ObligationStatus.Pending);
        await obligationRepo.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        await eventRepo.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task FulfillAsync_OnActive_WithMonthlyRecurrence_SpawnsFollowUpObligation()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Monthly license",
            Description = "Recurring monthly",
            ResponsibleParty = ResponsibleParty.Us,
            DeadlineFormula = "monthly",
            Status = ObligationStatus.Active,
            Recurrence = ObligationRecurrence.Monthly,
            NextDueDate = new DateOnly(2026, 4, 16),
            Amount = 1200m,
            Currency = "USD",
            AlertWindowDays = 14,
            GracePeriodDays = 3,
            BusinessDayCalendar = "US",
            Source = ObligationSource.Manual,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.FulfillAsync(existing.Id, notes: null, actor: "user:alice");

        result!.Status.Should().Be(ObligationStatus.Fulfilled);

        // Parent updated; new follow-up added with status=active and next_due_date+1 month.
        await obligationRepo.Received(1).AddAsync(Arg.Is<Obligation>(o =>
            o.Id != existing.Id
            && o.ContractId == existing.ContractId
            && o.TenantId == TenantA
            && o.Status == ObligationStatus.Active
            && o.Recurrence == ObligationRecurrence.Monthly
            && o.NextDueDate == new DateOnly(2026, 5, 16)
            && o.Title == existing.Title
            && o.Amount == existing.Amount
            && o.Source == existing.Source));

        // Two events total: parent (active→fulfilled) + follow-up (→active creation event).
        await eventRepo.Received(2).AddAsync(Arg.Any<ObligationEvent>());
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.ObligationId == existing.Id && e.FromStatus == "active" && e.ToStatus == "fulfilled"));
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.ObligationId != existing.Id
            && e.ToStatus == "active"
            && e.Actor == "system"
            && e.Reason != null
            && e.Reason!.Contains("recurring")
            && e.Metadata != null
            && e.Metadata!.ContainsKey("parent_id")));
    }

    [Fact]
    public async Task FulfillAsync_OnActive_WithAnnuallyRecurrence_ComputesYearAheadNextDueDate()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Reporting,
            Title = "Annual certification",
            Status = ObligationStatus.Active,
            Recurrence = ObligationRecurrence.Annually,
            NextDueDate = new DateOnly(2026, 4, 16),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        await service.FulfillAsync(existing.Id, notes: null, actor: "user:alice");

        await obligationRepo.Received(1).AddAsync(Arg.Is<Obligation>(o =>
            o.NextDueDate == new DateOnly(2027, 4, 16) && o.Status == ObligationStatus.Active));
    }

    [Fact]
    public async Task FulfillAsync_WithNullRecurrence_DoesNotSpawnFollowUp()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "No recurrence",
            Status = ObligationStatus.Active,
            Recurrence = null,
            NextDueDate = new DateOnly(2026, 5, 1),
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        await service.FulfillAsync(existing.Id, notes: null, actor: "user:alice");

        await obligationRepo.DidNotReceive().AddAsync(Arg.Any<Obligation>());
    }

    [Fact]
    public async Task WaiveAsync_OnActive_TransitionsAndWritesEventWithReason()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Waivable",
            Status = ObligationStatus.Active,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.WaiveAsync(existing.Id, reason: "executive override", actor: "user:bob");

        result!.Status.Should().Be(ObligationStatus.Waived);
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.FromStatus == "active"
            && e.ToStatus == "waived"
            && e.Actor == "user:bob"
            && e.Reason == "executive override"));
    }

    [Fact]
    public async Task WaiveAsync_OnPending_Throws()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Not waivable from pending",
            Status = ObligationStatus.Pending,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.WaiveAsync(existing.Id, reason: "x", actor: "user:bob");

        await act.Should().ThrowAsync<ObligationTransitionException>();
    }

    [Fact]
    public async Task DisputeAsync_OnActive_TransitionsToDisputedWithReason()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Dispute me",
            Status = ObligationStatus.Active,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.DisputeAsync(existing.Id, reason: "invoice mismatch", actor: "user:alice");

        result!.Status.Should().Be(ObligationStatus.Disputed);
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.FromStatus == "active"
            && e.ToStatus == "disputed"
            && e.Reason == "invoice mismatch"));
    }

    [Fact]
    public async Task DisputeAsync_OnFulfilled_Throws()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Terminal",
            Status = ObligationStatus.Fulfilled,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.DisputeAsync(existing.Id, reason: "x", actor: "user:alice");

        await act.Should().ThrowAsync<ObligationTransitionException>();
    }

    [Fact]
    public async Task ResolveDisputeAsync_Stands_MovesDisputedBackToActive()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Disputed",
            Status = ObligationStatus.Disputed,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.ResolveDisputeAsync(
            existing.Id,
            DisputeResolution.Stands,
            notes: "investigator confirmed owe",
            actor: "user:alice");

        result!.Status.Should().Be(ObligationStatus.Active);
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.FromStatus == "disputed"
            && e.ToStatus == "active"
            && e.Reason != null
            && e.Reason!.Contains("stands")));
    }

    [Fact]
    public async Task ResolveDisputeAsync_Waived_MovesDisputedToWaived()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Disputed",
            Status = ObligationStatus.Disputed,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var result = await service.ResolveDisputeAsync(
            existing.Id,
            DisputeResolution.Waived,
            notes: null,
            actor: "user:alice");

        result!.Status.Should().Be(ObligationStatus.Waived);
        await eventRepo.Received(1).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.FromStatus == "disputed" && e.ToStatus == "waived"));
    }

    [Fact]
    public async Task ResolveDisputeAsync_OnActive_Throws()
    {
        var (service, obligationRepo, _, _, _, _) = BuildHarness();
        var existing = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Not disputed",
            Status = ObligationStatus.Active,
        };
        obligationRepo.GetByIdAsync(existing.Id).Returns(existing);

        var act = () => service.ResolveDisputeAsync(
            existing.Id, DisputeResolution.Stands, notes: null, actor: "user:alice");

        await act.Should().ThrowAsync<ObligationTransitionException>();
    }

    [Fact]
    public async Task ExpireDueToContractArchiveAsync_ExpiresOnlyNonTerminalObligations()
    {
        var (service, obligationRepo, eventRepo, _, _, _) = BuildHarness();
        var contractId = Guid.NewGuid();

        var active1 = MakeObligation(contractId, ObligationStatus.Active);
        var active2 = MakeObligation(contractId, ObligationStatus.Upcoming);
        var fulfilled = MakeObligation(contractId, ObligationStatus.Fulfilled);
        var waived = MakeObligation(contractId, ObligationStatus.Waived);

        // Repo returns ALL obligations (terminal + non-terminal) for the contract; service
        // filters terminal ones out before calling the state machine.
        obligationRepo.ListAsync(
                Arg.Is<ObligationFilters>(f => f.ContractId == contractId),
                Arg.Any<PageRequest>())
            .Returns(new PagedResult<Obligation>(
                new[] { active1, active2, fulfilled, waived },
                new PaginationMetadata(null, false, 4)));

        await service.ExpireDueToContractArchiveAsync(contractId, actor: "system:archive_cascade");

        // Exactly 2 obligations updated (the two non-terminal ones).
        await obligationRepo.Received(1).UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == active1.Id && o.Status == ObligationStatus.Expired));
        await obligationRepo.Received(1).UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == active2.Id && o.Status == ObligationStatus.Expired));
        await obligationRepo.DidNotReceive().UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == fulfilled.Id));
        await obligationRepo.DidNotReceive().UpdateAsync(Arg.Is<Obligation>(o =>
            o.Id == waived.Id));

        // Two events written, both with actor=system:archive_cascade and metadata.contract_id.
        await eventRepo.Received(2).AddAsync(Arg.Is<ObligationEvent>(e =>
            e.ToStatus == "expired"
            && e.Actor == "system:archive_cascade"
            && e.Metadata != null
            && e.Metadata!.ContainsKey("contract_id")));
    }

    [Fact]
    public async Task ListEventsAsync_DelegatesToRepository()
    {
        var (service, _, eventRepo, _, _, _) = BuildHarness();
        var oblId = Guid.NewGuid();
        var req = new PageRequest { PageSize = 10 };
        var page = new PagedResult<ObligationEvent>(
            Array.Empty<ObligationEvent>(), new PaginationMetadata(null, false, 0));
        eventRepo.ListByObligationAsync(oblId, req, Arg.Any<CancellationToken>()).Returns(page);

        var result = await service.ListEventsAsync(oblId, req);

        result.Should().BeSameAs(page);
        await eventRepo.Received(1).ListByObligationAsync(oblId, req, Arg.Any<CancellationToken>());
    }

    private static Obligation MakeObligation(Guid contractId, ObligationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        ContractId = contractId,
        ObligationType = ObligationType.Payment,
        Title = $"Ob {Guid.NewGuid()}",
        Status = status,
    };
}
