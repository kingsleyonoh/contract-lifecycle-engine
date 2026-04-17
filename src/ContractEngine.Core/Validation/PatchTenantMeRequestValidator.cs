using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// FluentValidation rules for <c>PATCH /api/tenants/me</c>. Every field is optional (absence means
/// "don't touch"), but any field that IS provided must validate to the same rules as registration:
/// name ≤ 255, IANA timezone, 3-letter currency. Validators live in Core so the DI assembly scan
/// in <c>ServiceRegistration</c> picks them up with no additional wiring.
/// </summary>
public sealed class PatchTenantMeRequestValidator : AbstractValidator<PatchTenantMeRequestDto>
{
    public PatchTenantMeRequestValidator()
    {
        When(x => x.Name is not null, () =>
        {
            RuleFor(x => x.Name!)
                .NotEmpty().WithMessage("name, if provided, must not be blank")
                .MaximumLength(255).WithMessage("name must be 255 characters or fewer");
        });

        When(x => !string.IsNullOrWhiteSpace(x.DefaultTimezone), () =>
        {
            RuleFor(x => x.DefaultTimezone!)
                .Must(BeValidTimezone)
                .WithMessage("default_timezone must be a valid IANA timezone (e.g. 'UTC', 'US/Eastern')");
        });

        When(x => !string.IsNullOrWhiteSpace(x.DefaultCurrency), () =>
        {
            RuleFor(x => x.DefaultCurrency!)
                .Length(3).WithMessage("default_currency must be a 3-letter ISO 4217 code");
        });
    }

    private static bool BeValidTimezone(string value)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// Validator-side DTO mirror so the validator stays free of Api-layer JSON types.
/// </summary>
public sealed record PatchTenantMeRequestDto(
    string? Name,
    string? DefaultTimezone,
    string? DefaultCurrency,
    Dictionary<string, object>? Metadata);
