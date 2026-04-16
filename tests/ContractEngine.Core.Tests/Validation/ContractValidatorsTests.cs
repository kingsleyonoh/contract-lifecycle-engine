using ContractEngine.Core.Enums;
using ContractEngine.Core.Services;
using ContractEngine.Core.Validation;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Validation;

/// <summary>
/// FluentValidation rule coverage for <see cref="CreateContractRequestValidator"/> and
/// <see cref="UpdateContractRequestValidator"/>. Tests every rule at least once with a pass and a
/// fail case so regressions surface immediately.
/// </summary>
public class ContractValidatorsTests
{
    private readonly CreateContractRequestValidator _create = new();
    private readonly UpdateContractRequestValidator _update = new();

    [Fact]
    public void Create_ValidPayload_Passes()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "Valid Contract",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            EffectiveDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2027, 1, 1),
        });

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Create_MissingTitle_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateContractRequest.Title));
    }

    [Fact]
    public void Create_TitleTooLong_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = new string('x', 501),
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_BothCounterpartyIdAndName_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            CounterpartyName = "Also a name",
            ContractType = ContractType.Vendor,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_NeitherCounterpartyIdNorName_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            ContractType = ContractType.Vendor,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_OnlyCounterpartyName_Passes()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyName = "New Counterparty",
            ContractType = ContractType.Partnership,
        });

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void Create_EndDateBeforeEffectiveDate_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            EffectiveDate = new DateOnly(2027, 1, 1),
            EndDate = new DateOnly(2026, 1, 1),
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("end_date"));
    }

    [Fact]
    public void Create_EndDateEqualsEffectiveDate_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            EffectiveDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 1),
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_AutoRenewalWithoutPeriodMonths_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            AutoRenewal = true,
            AutoRenewalPeriodMonths = null,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_AutoRenewalWithZeroMonths_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            AutoRenewal = true,
            AutoRenewalPeriodMonths = 0,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidCurrencyLength_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            Currency = "DOLLARS",
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_NegativeTotalValue_Fails()
    {
        var result = _create.Validate(new CreateContractRequest
        {
            Title = "X",
            CounterpartyId = Guid.NewGuid(),
            ContractType = ContractType.Vendor,
            TotalValue = -10m,
        });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Update_AllFieldsNull_Passes()
    {
        var result = _update.Validate(new UpdateContractRequest());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Update_BlankTitle_Fails()
    {
        var result = _update.Validate(new UpdateContractRequest { Title = "" });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Update_EndBeforeEffective_Fails()
    {
        var result = _update.Validate(new UpdateContractRequest
        {
            EffectiveDate = new DateOnly(2027, 1, 1),
            EndDate = new DateOnly(2026, 1, 1),
        });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Update_AutoRenewalTrueWithoutMonths_Fails()
    {
        var result = _update.Validate(new UpdateContractRequest { AutoRenewal = true });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Update_AutoRenewalMonthsZero_Fails()
    {
        var result = _update.Validate(new UpdateContractRequest { AutoRenewalPeriodMonths = 0 });
        result.IsValid.Should().BeFalse();
    }
}
