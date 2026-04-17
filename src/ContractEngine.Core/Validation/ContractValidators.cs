using ContractEngine.Core.Services;
using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// FluentValidation rules for <c>POST /api/contracts</c>. Title required ≤500 chars; exactly one
/// of <c>counterparty_id</c> or <c>counterparty_name</c> must be supplied; <c>end_date</c> must be
/// strictly after <c>effective_date</c> when both are set; <c>auto_renewal_period_months</c> must
/// be supplied (and &gt; 0) whenever <c>auto_renewal</c> is <c>true</c>.
/// </summary>
public sealed class CreateContractRequestValidator : AbstractValidator<CreateContractRequest>
{
    public CreateContractRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("title is required")
            .MaximumLength(500).WithMessage("title must be 500 characters or fewer");

        When(x => !string.IsNullOrWhiteSpace(x.ReferenceNumber), () =>
        {
            RuleFor(x => x.ReferenceNumber!)
                .MaximumLength(100).WithMessage("reference_number must be 100 characters or fewer");
        });

        // Exactly one of counterparty_id OR counterparty_name — prevents ambiguity around whether
        // the server should look up vs create the counterparty.
        RuleFor(x => x)
            .Must(HasExactlyOneCounterpartyIdentifier)
            .WithName("counterparty")
            .WithMessage("provide counterparty_id OR counterparty_name, not both — and at least one is required");

        When(x => !string.IsNullOrWhiteSpace(x.CounterpartyName), () =>
        {
            RuleFor(x => x.CounterpartyName!)
                .MaximumLength(255).WithMessage("counterparty_name must be 255 characters or fewer");
        });

        When(x => x.EffectiveDate is not null && x.EndDate is not null, () =>
        {
            RuleFor(x => x)
                .Must(x => x.EndDate > x.EffectiveDate)
                .WithName("end_date")
                .WithMessage("end_date must be strictly after effective_date");
        });

        When(x => x.AutoRenewal == true, () =>
        {
            RuleFor(x => x.AutoRenewalPeriodMonths)
                .NotNull().WithMessage("auto_renewal_period_months is required when auto_renewal is true")
                .GreaterThan(0).WithMessage("auto_renewal_period_months must be greater than zero");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Currency), () =>
        {
            RuleFor(x => x.Currency!)
                .Length(3).WithMessage("currency must be a 3-letter ISO 4217 code");
        });

        When(x => x.TotalValue is not null, () =>
        {
            RuleFor(x => x.TotalValue!.Value)
                .GreaterThanOrEqualTo(0m).WithMessage("total_value must be non-negative");
        });

        When(x => x.RenewalNoticeDays is not null, () =>
        {
            RuleFor(x => x.RenewalNoticeDays!.Value)
                .GreaterThanOrEqualTo(0).WithMessage("renewal_notice_days must be non-negative");
        });

        When(x => !string.IsNullOrWhiteSpace(x.GoverningLaw), () =>
        {
            RuleFor(x => x.GoverningLaw!)
                .MaximumLength(100).WithMessage("governing_law must be 100 characters or fewer");
        });

        // Batch 026 security-audit finding F: public callers MUST NOT set engine-reserved metadata
        // keys (webhook_envelope_id, webhook_document_id, etc.) — those are load-bearing for webhook
        // idempotency and provenance. The webhook helper bypasses this validator and writes those
        // keys directly post-HMAC, so only public-API callers are blocked here.
        When(x => x.Metadata is { Count: > 0 }, () =>
        {
            RuleFor(x => x.Metadata!)
                .Must(HaveNoReservedMetadataKeys)
                .OverridePropertyName("metadata")
                .WithMessage($"metadata keys are reserved by the engine and cannot be set via the public API: {string.Join(", ", ContractMetadataReservedKeys.All)}");
        });
    }

    private static bool HasExactlyOneCounterpartyIdentifier(CreateContractRequest request)
    {
        var hasId = request.CounterpartyId is not null && request.CounterpartyId != Guid.Empty;
        var hasName = !string.IsNullOrWhiteSpace(request.CounterpartyName);
        return hasId ^ hasName;
    }

    private static bool HaveNoReservedMetadataKeys(Dictionary<string, object> metadata) =>
        !metadata.Keys.Any(k => ContractMetadataReservedKeys.All.Contains(k));
}

/// <summary>
/// FluentValidation rules for <c>PATCH /api/contracts/{id}</c>. Every field is optional (absence
/// = untouched). A field that IS present must still validate to the same rules as creation.
/// Status is NOT accepted — callers use the lifecycle endpoints; the endpoint layer rejects
/// status with a 422 CONFLICT.
/// </summary>
public sealed class UpdateContractRequestValidator : AbstractValidator<UpdateContractRequest>
{
    public UpdateContractRequestValidator()
    {
        When(x => x.Title is not null, () =>
        {
            RuleFor(x => x.Title!)
                .NotEmpty().WithMessage("title, if provided, must not be blank")
                .MaximumLength(500).WithMessage("title must be 500 characters or fewer");
        });

        When(x => x.ReferenceNumber is not null, () =>
        {
            RuleFor(x => x.ReferenceNumber!)
                .MaximumLength(100).WithMessage("reference_number must be 100 characters or fewer");
        });

        When(x => x.EffectiveDate is not null && x.EndDate is not null, () =>
        {
            RuleFor(x => x)
                .Must(x => x.EndDate > x.EffectiveDate)
                .WithName("end_date")
                .WithMessage("end_date must be strictly after effective_date");
        });

        When(x => x.AutoRenewal == true, () =>
        {
            RuleFor(x => x.AutoRenewalPeriodMonths)
                .NotNull().WithMessage("auto_renewal_period_months is required when auto_renewal is true")
                .GreaterThan(0).WithMessage("auto_renewal_period_months must be greater than zero");
        });

        When(x => x.AutoRenewalPeriodMonths is not null, () =>
        {
            RuleFor(x => x.AutoRenewalPeriodMonths!.Value)
                .GreaterThan(0).WithMessage("auto_renewal_period_months must be greater than zero");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Currency), () =>
        {
            RuleFor(x => x.Currency!)
                .Length(3).WithMessage("currency must be a 3-letter ISO 4217 code");
        });

        When(x => x.TotalValue is not null, () =>
        {
            RuleFor(x => x.TotalValue!.Value)
                .GreaterThanOrEqualTo(0m).WithMessage("total_value must be non-negative");
        });

        When(x => x.RenewalNoticeDays is not null, () =>
        {
            RuleFor(x => x.RenewalNoticeDays!.Value)
                .GreaterThanOrEqualTo(0).WithMessage("renewal_notice_days must be non-negative");
        });

        When(x => !string.IsNullOrWhiteSpace(x.GoverningLaw), () =>
        {
            RuleFor(x => x.GoverningLaw!)
                .MaximumLength(100).WithMessage("governing_law must be 100 characters or fewer");
        });

        // Batch 026 security-audit finding F — same rationale as CreateContractRequestValidator.
        When(x => x.Metadata is { Count: > 0 }, () =>
        {
            RuleFor(x => x.Metadata!)
                .Must(HaveNoReservedMetadataKeys)
                .OverridePropertyName("metadata")
                .WithMessage($"metadata keys are reserved by the engine and cannot be set via the public API: {string.Join(", ", ContractMetadataReservedKeys.All)}");
        });
    }

    private static bool HaveNoReservedMetadataKeys(Dictionary<string, object> metadata) =>
        !metadata.Keys.Any(k => ContractMetadataReservedKeys.All.Contains(k));
}
