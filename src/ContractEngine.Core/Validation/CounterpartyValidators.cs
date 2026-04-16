using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// FluentValidation rules for creating a counterparty. Name is required; all other fields are
/// optional but length-bounded to match PRD §4.2 column widths. Email is validated with the
/// built-in FluentValidation email check when provided.
/// </summary>
public sealed class CreateCounterpartyRequestValidator : AbstractValidator<CreateCounterpartyRequestDto>
{
    public CreateCounterpartyRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("name is required")
            .MaximumLength(255).WithMessage("name must be 255 characters or fewer");

        When(x => !string.IsNullOrWhiteSpace(x.LegalName), () =>
        {
            RuleFor(x => x.LegalName!)
                .MaximumLength(255).WithMessage("legal_name must be 255 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Industry), () =>
        {
            RuleFor(x => x.Industry!)
                .MaximumLength(100).WithMessage("industry must be 100 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.ContactEmail), () =>
        {
            RuleFor(x => x.ContactEmail!)
                .EmailAddress().WithMessage("contact_email must be a valid email address")
                .MaximumLength(255).WithMessage("contact_email must be 255 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.ContactName), () =>
        {
            RuleFor(x => x.ContactName!)
                .MaximumLength(255).WithMessage("contact_name must be 255 characters or fewer");
        });
    }
}

/// <summary>
/// FluentValidation rules for PATCH. Every field is optional (absence = "don't touch"). A field
/// that IS present must still validate to the same rules as creation. An explicit empty string on
/// <c>name</c> is rejected — use PATCH to change the value, not clear it.
/// </summary>
public sealed class UpdateCounterpartyRequestValidator : AbstractValidator<UpdateCounterpartyRequestDto>
{
    public UpdateCounterpartyRequestValidator()
    {
        When(x => x.Name is not null, () =>
        {
            RuleFor(x => x.Name!)
                .NotEmpty().WithMessage("name, if provided, must not be blank")
                .MaximumLength(255).WithMessage("name must be 255 characters or fewer");
        });

        When(x => x.LegalName is not null, () =>
        {
            RuleFor(x => x.LegalName!)
                .MaximumLength(255).WithMessage("legal_name must be 255 characters or fewer");
        });

        When(x => x.Industry is not null, () =>
        {
            RuleFor(x => x.Industry!)
                .MaximumLength(100).WithMessage("industry must be 100 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.ContactEmail), () =>
        {
            RuleFor(x => x.ContactEmail!)
                .EmailAddress().WithMessage("contact_email must be a valid email address")
                .MaximumLength(255).WithMessage("contact_email must be 255 characters or fewer");
        });

        When(x => x.ContactName is not null, () =>
        {
            RuleFor(x => x.ContactName!)
                .MaximumLength(255).WithMessage("contact_name must be 255 characters or fewer");
        });
    }
}

/// <summary>Validator-side DTO mirror for create requests.</summary>
public sealed record CreateCounterpartyRequestDto(
    string Name,
    string? LegalName,
    string? Industry,
    string? ContactEmail,
    string? ContactName,
    string? Notes);

/// <summary>Validator-side DTO mirror for PATCH requests.</summary>
public sealed record UpdateCounterpartyRequestDto(
    string? Name,
    string? LegalName,
    string? Industry,
    string? ContactEmail,
    string? ContactName,
    string? Notes);
