using ContractEngine.Core.Enums;
using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// Domain-side record that endpoints map into before running
/// <see cref="CreateObligationRequestValidator"/>. Mirrors the Api-layer
/// <c>CreateObligationRequest</c> but keeps Core free of an Api-project dependency. The endpoint
/// (Batch 012) maps the wire DTO into this record before validation.
///
/// <para><see cref="ResponsibleParty"/> stays a string here (not the enum) so the validator can
/// report a readable "must be one of us/counterparty/both" message when the caller sends an
/// invalid token — FluentValidation cannot enumerate enum values as nicely.</para>
/// </summary>
public sealed record CreateObligationRequestDomain
{
    public Guid ContractId { get; init; }
    public ObligationType ObligationType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ResponsibleParty { get; init; }
    public DateOnly? DeadlineDate { get; init; }
    public string? DeadlineFormula { get; init; }
    public ObligationRecurrence? Recurrence { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public int? AlertWindowDays { get; init; }
    public int? GracePeriodDays { get; init; }
    public string? BusinessDayCalendar { get; init; }
    public string? ClauseReference { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// FluentValidation rules for <c>POST /api/obligations</c> (PRD §5.3 inputs). Key rules:
/// <list type="bullet">
///   <item><c>title</c> required, 1–500 chars.</item>
///   <item><c>contract_id</c> required (non-empty Guid).</item>
///   <item>At least one of <c>deadline_date</c>, <c>deadline_formula</c>, or <c>recurrence</c>
///     must be supplied so the obligation has a computable schedule.</item>
///   <item><c>amount</c> non-negative when provided.</item>
///   <item><c>currency</c>, when provided, must be 3 characters.</item>
///   <item><c>alert_window_days</c> / <c>grace_period_days</c> non-negative when provided.</item>
///   <item><c>responsible_party</c>, when provided, must be <c>us</c>, <c>counterparty</c>, or
///     <c>both</c>.</item>
/// </list>
/// </summary>
public sealed class CreateObligationRequestValidator : AbstractValidator<CreateObligationRequestDomain>
{
    private static readonly string[] AllowedResponsibleParties = { "us", "counterparty", "both" };

    public CreateObligationRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("title is required")
            .MaximumLength(500).WithMessage("title must be 500 characters or fewer");

        RuleFor(x => x.ContractId)
            .NotEmpty().WithMessage("contract_id is required");

        // At least one scheduling signal must be present. A pure "pending" obligation with nothing
        // to schedule against would never surface on a deadline scan.
        RuleFor(x => x)
            .Must(HasAnySchedulingSignal)
            .WithName("deadline")
            .WithMessage("at least one of deadline_date, deadline_formula, or recurrence is required");

        When(x => x.Description is not null, () =>
        {
            RuleFor(x => x.Description!)
                .MaximumLength(5000).WithMessage("description must be 5000 characters or fewer");
        });

        When(x => x.Amount is not null, () =>
        {
            RuleFor(x => x.Amount!.Value)
                .GreaterThanOrEqualTo(0m).WithMessage("amount must be non-negative");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Currency), () =>
        {
            RuleFor(x => x.Currency!)
                .Length(3).WithMessage("currency must be a 3-letter ISO 4217 code");
        });

        When(x => x.AlertWindowDays is not null, () =>
        {
            RuleFor(x => x.AlertWindowDays!.Value)
                .GreaterThanOrEqualTo(0).WithMessage("alert_window_days must be non-negative");
        });

        When(x => x.GracePeriodDays is not null, () =>
        {
            RuleFor(x => x.GracePeriodDays!.Value)
                .GreaterThanOrEqualTo(0).WithMessage("grace_period_days must be non-negative");
        });

        When(x => x.DeadlineFormula is not null, () =>
        {
            RuleFor(x => x.DeadlineFormula!)
                .MaximumLength(255).WithMessage("deadline_formula must be 255 characters or fewer");
        });

        When(x => x.ClauseReference is not null, () =>
        {
            RuleFor(x => x.ClauseReference!)
                .MaximumLength(255).WithMessage("clause_reference must be 255 characters or fewer");
        });

        When(x => x.BusinessDayCalendar is not null, () =>
        {
            RuleFor(x => x.BusinessDayCalendar!)
                .MaximumLength(50).WithMessage("business_day_calendar must be 50 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.ResponsibleParty), () =>
        {
            RuleFor(x => x.ResponsibleParty!)
                .Must(p => AllowedResponsibleParties.Contains(p, StringComparer.OrdinalIgnoreCase))
                .WithMessage("responsible_party must be one of: us, counterparty, both");
        });
    }

    private static bool HasAnySchedulingSignal(CreateObligationRequestDomain request)
    {
        return request.DeadlineDate is not null
            || !string.IsNullOrWhiteSpace(request.DeadlineFormula)
            || request.Recurrence is not null;
    }
}
