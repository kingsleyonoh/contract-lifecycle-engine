using ContractEngine.Core.Defaults;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Defaults;

/// <summary>
/// Unit tests for <see cref="ExtractionDefaults"/> — the hardcoded fallback extraction prompts
/// (PRD §5.2). Ensures all four prompt types are present, non-empty, and the lookup helper
/// returns null for unknown types.
/// </summary>
public class ExtractionDefaultsTests
{
    [Theory]
    [InlineData("payment")]
    [InlineData("renewal")]
    [InlineData("compliance")]
    [InlineData("performance")]
    public void GetByType_KnownTypes_ReturnsNonEmptyPrompt(string promptType)
    {
        var result = ExtractionDefaults.GetByType(promptType);

        result.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetByType_Payment_ContainsPaymentObligation()
    {
        var result = ExtractionDefaults.GetByType("payment");

        result.Should().Contain("payment obligations", because: "PRD §5.2 defines this prompt");
    }

    [Fact]
    public void GetByType_Renewal_ContainsRenewalClause()
    {
        var result = ExtractionDefaults.GetByType("renewal");

        result.Should().Contain("renewal", because: "PRD §5.2 defines this prompt");
        result.Should().Contain("termination", because: "PRD §5.2 defines this prompt");
    }

    [Fact]
    public void GetByType_Compliance_ContainsComplianceObligation()
    {
        var result = ExtractionDefaults.GetByType("compliance");

        result.Should().Contain("compliance", because: "PRD §5.2 defines this prompt");
    }

    [Fact]
    public void GetByType_Performance_ContainsSla()
    {
        var result = ExtractionDefaults.GetByType("performance");

        result.Should().Contain("SLA", because: "PRD §5.2 defines this prompt");
    }

    [Fact]
    public void GetByType_UnknownType_ReturnsNull()
    {
        var result = ExtractionDefaults.GetByType("unknown_type");

        result.Should().BeNull();
    }

    [Fact]
    public void GetByType_EmptyString_ReturnsNull()
    {
        var result = ExtractionDefaults.GetByType("");

        result.Should().BeNull();
    }

    [Fact]
    public void GetByType_CaseInsensitive_ReturnsPrompt()
    {
        var upper = ExtractionDefaults.GetByType("PAYMENT");
        var mixed = ExtractionDefaults.GetByType("Payment");

        upper.Should().NotBeNull();
        mixed.Should().NotBeNull();
        upper.Should().Be(mixed);
    }

    [Fact]
    public void AllPromptTypes_AreFour()
    {
        ExtractionDefaults.AllPromptTypes.Should().HaveCount(4);
        ExtractionDefaults.AllPromptTypes.Should().BeEquivalentTo(
            new[] { "payment", "renewal", "compliance", "performance" });
    }
}
