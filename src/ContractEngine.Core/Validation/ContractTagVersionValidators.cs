using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// FluentValidation rules for the tag-replace request body (Batch 010). The <c>tags</c> list
/// itself is required (null rejected with 400), but an EMPTY list is valid — that's the idempotent
/// clear-all semantics documented in <c>ContractTagService</c>. Each tag must be 1-100 chars.
/// </summary>
public sealed class PutTagsRequestValidator : AbstractValidator<PutTagsRequestDomain>
{
    public const int MaxTagLength = 100;

    public PutTagsRequestValidator()
    {
        RuleFor(x => x.Tags)
            .NotNull().WithMessage("tags array is required (use [] to clear all tags)");

        RuleForEach(x => x.Tags!)
            .NotNull().WithMessage("tag value cannot be null")
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("tag value cannot be empty or whitespace")
            .Must(t => (t ?? string.Empty).Trim().Length <= MaxTagLength)
            .WithMessage($"tag value exceeds {MaxTagLength}-character limit");
    }
}

/// <summary>
/// Domain-side record that the endpoint maps into before running <see cref="PutTagsRequestValidator"/>.
/// Kept in Core so the validator does not depend on the Api-layer DTO.
/// </summary>
public sealed record PutTagsRequestDomain
{
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// FluentValidation rules for the create-version request body. All fields are optional; the only
/// rules are upper bounds on the text fields so pathological input cannot blow out the DB columns.
/// </summary>
public sealed class CreateVersionRequestValidator : AbstractValidator<CreateVersionRequestDomain>
{
    public const int MaxCreatedByLength = 255;

    public CreateVersionRequestValidator()
    {
        When(x => x.ChangeSummary is not null, () =>
        {
            RuleFor(x => x.ChangeSummary!)
                .NotEmpty().WithMessage("change_summary, if provided, must not be blank");
        });

        When(x => !string.IsNullOrWhiteSpace(x.CreatedBy), () =>
        {
            RuleFor(x => x.CreatedBy!)
                .MaximumLength(MaxCreatedByLength)
                .WithMessage($"created_by must be {MaxCreatedByLength} characters or fewer");
        });
    }
}

/// <summary>Domain-side record for the version request validator.</summary>
public sealed record CreateVersionRequestDomain
{
    public string? ChangeSummary { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public string? CreatedBy { get; init; }
}
