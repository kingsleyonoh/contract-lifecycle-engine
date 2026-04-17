using ContractEngine.Core.Enums;
using ContractEngine.Core.Validation;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Validation;

/// <summary>
/// FluentValidation rule coverage for <see cref="CreateObligationRequestValidator"/>. Exercises
/// every rule at least once with a pass and a fail case so regressions surface immediately.
/// </summary>
public class ObligationValidatorTests
{
    private readonly CreateObligationRequestValidator _validator = new();

    private static CreateObligationRequestDomain Valid(Action<Builder>? configure = null)
    {
        var b = new Builder
        {
            ContractId = Guid.NewGuid(),
            ObligationType = ObligationType.Payment,
            Title = "Valid obligation",
            DeadlineDate = new DateOnly(2026, 6, 1),
        };
        configure?.Invoke(b);
        return b.Build();
    }

    [Fact]
    public void ValidPayload_Passes()
    {
        var result = _validator.Validate(Valid());
        result.IsValid.Should().BeTrue(
            because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void MissingTitle_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Title = ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateObligationRequestDomain.Title));
    }

    [Fact]
    public void TitleOver500Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Title = new string('x', 501)));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("500"));
    }

    [Fact]
    public void TitleExactly500Chars_Passes()
    {
        var result = _validator.Validate(Valid(b => b.Title = new string('x', 500)));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ContractIdEmpty_Fails()
    {
        var result = _validator.Validate(Valid(b => b.ContractId = Guid.Empty));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateObligationRequestDomain.ContractId));
    }

    [Fact]
    public void NoSchedulingSignal_Fails()
    {
        var result = _validator.Validate(Valid(b =>
        {
            b.DeadlineDate = null;
            b.DeadlineFormula = null;
            b.Recurrence = null;
        }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("deadline_date"));
    }

    [Fact]
    public void OnlyDeadlineFormula_Passes()
    {
        var result = _validator.Validate(Valid(b =>
        {
            b.DeadlineDate = null;
            b.DeadlineFormula = "contract.end_date - 90d";
        }));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void OnlyRecurrence_Passes()
    {
        var result = _validator.Validate(Valid(b =>
        {
            b.DeadlineDate = null;
            b.Recurrence = ObligationRecurrence.Monthly;
        }));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void NegativeAmount_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Amount = -0.01m));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("amount"));
    }

    [Fact]
    public void ZeroAmount_Passes()
    {
        var result = _validator.Validate(Valid(b => b.Amount = 0m));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void AmountNull_Passes()
    {
        var result = _validator.Validate(Valid(b => b.Amount = null));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Currency2Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Currency = "US"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Currency3Chars_Passes()
    {
        var result = _validator.Validate(Valid(b => b.Currency = "USD"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Currency4Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Currency = "USDX"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CurrencyNull_Passes()
    {
        var result = _validator.Validate(Valid(b => b.Currency = null));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void NegativeAlertWindowDays_Fails()
    {
        var result = _validator.Validate(Valid(b => b.AlertWindowDays = -1));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("alert_window_days"));
    }

    [Fact]
    public void ZeroAlertWindowDays_Passes()
    {
        var result = _validator.Validate(Valid(b => b.AlertWindowDays = 0));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void NegativeGracePeriodDays_Fails()
    {
        var result = _validator.Validate(Valid(b => b.GracePeriodDays = -1));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("grace_period_days"));
    }

    [Fact]
    public void ResponsiblePartyInvalid_Fails()
    {
        var result = _validator.Validate(Valid(b => b.ResponsibleParty = "someone-else"));
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("us")]
    [InlineData("counterparty")]
    [InlineData("both")]
    [InlineData("US")]  // case-insensitive per validator
    [InlineData("Counterparty")]
    public void ResponsiblePartyValid_Passes(string rp)
    {
        var result = _validator.Validate(Valid(b => b.ResponsibleParty = rp));
        result.IsValid.Should().BeTrue(
            because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void DeadlineFormulaOver255Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.DeadlineFormula = new string('x', 256)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void DescriptionOver5000Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.Description = new string('x', 5001)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ClauseReferenceOver255Chars_Fails()
    {
        var result = _validator.Validate(Valid(b => b.ClauseReference = new string('x', 256)));
        result.IsValid.Should().BeFalse();
    }

    private sealed class Builder
    {
        public Guid ContractId { get; set; }
        public ObligationType ObligationType { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? ResponsibleParty { get; set; }
        public DateOnly? DeadlineDate { get; set; }
        public string? DeadlineFormula { get; set; }
        public ObligationRecurrence? Recurrence { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
        public int? AlertWindowDays { get; set; }
        public int? GracePeriodDays { get; set; }
        public string? BusinessDayCalendar { get; set; }
        public string? ClauseReference { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }

        public CreateObligationRequestDomain Build() => new()
        {
            ContractId = ContractId,
            ObligationType = ObligationType,
            Title = Title,
            Description = Description,
            ResponsibleParty = ResponsibleParty,
            DeadlineDate = DeadlineDate,
            DeadlineFormula = DeadlineFormula,
            Recurrence = Recurrence,
            Amount = Amount,
            Currency = Currency,
            AlertWindowDays = AlertWindowDays,
            GracePeriodDays = GracePeriodDays,
            BusinessDayCalendar = BusinessDayCalendar,
            ClauseReference = ClauseReference,
            Metadata = Metadata,
        };
    }
}
