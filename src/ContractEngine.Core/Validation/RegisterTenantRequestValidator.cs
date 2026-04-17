using FluentValidation;

namespace ContractEngine.Core.Validation;

/// <summary>
/// FluentValidation rules for the public tenant registration request. Kept in <c>Core</c> so
/// both <c>Api</c> endpoints and future CLI/seed code can reuse it. IANA timezone validation
/// goes through <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>, which works on both Windows
/// and Linux once the <c>TimeZoneInfo</c> ICU support is loaded (default in .NET 8 SDK images).
///
/// Spec: PRD §8b (name, default_timezone, default_currency fields).
/// </summary>
public sealed class RegisterTenantRequestValidator : AbstractValidator<RegisterTenantRequestDto>
{
    public RegisterTenantRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("name is required")
            .MaximumLength(255).WithMessage("name must be 255 characters or fewer");

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
/// <para>Validator DTO mirror — keeps the validator in <c>Core</c> free from Api project
/// types. The API layer maps its JSON-bound DTO into this record before calling the validator.</para>
/// </summary>
public sealed record RegisterTenantRequestDto(string Name, string? DefaultTimezone, string? DefaultCurrency);
