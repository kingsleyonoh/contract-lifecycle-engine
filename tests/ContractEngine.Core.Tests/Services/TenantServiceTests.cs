using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace ContractEngine.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TenantService"/>. Mocks <see cref="ITenantRepository"/> via NSubstitute
/// (only external dependency — the DB). Verifies API key generation format, SHA-256 hashing,
/// prefix derivation, and repository interactions.
/// </summary>
public class TenantServiceTests
{
    private static readonly Regex ApiKeyPattern = new("^cle_live_[a-f0-9]{32}$", RegexOptions.Compiled);

    [Fact]
    public async Task RegisterAsync_ReturnsPlaintextApiKey_MatchingCleLiveHexPattern()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", null, null);

        result.PlaintextApiKey.Should().MatchRegex(ApiKeyPattern);
    }

    [Fact]
    public async Task RegisterAsync_StoresSha256HashOfPlaintextKey()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", null, null);

        var expectedHash = Sha256Hex(result.PlaintextApiKey);
        result.Tenant.ApiKeyHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task RegisterAsync_StoresFirst12CharsAsPrefix()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", null, null);

        // First 12 chars of "cle_live_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" → "cle_live_XXX"
        result.Tenant.ApiKeyPrefix.Should().HaveLength(12);
        result.PlaintextApiKey.Should().StartWith(result.Tenant.ApiKeyPrefix);
    }

    [Fact]
    public async Task RegisterAsync_InvokesAddAsyncExactlyOnce_WithTenantFieldsMatchingInputs()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", "US/Eastern", "EUR");

        await repository.Received(1).AddAsync(Arg.Is<Tenant>(t =>
            t.Name == "Acme Corp" &&
            t.DefaultTimezone == "US/Eastern" &&
            t.DefaultCurrency == "EUR"));
        result.Tenant.Name.Should().Be("Acme Corp");
        result.Tenant.DefaultTimezone.Should().Be("US/Eastern");
        result.Tenant.DefaultCurrency.Should().Be("EUR");
    }

    [Fact]
    public async Task RegisterAsync_AppliesDefaultTimezoneAndCurrency_WhenArgumentsAreNull()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", null, null);

        result.Tenant.DefaultTimezone.Should().Be("UTC");
        result.Tenant.DefaultCurrency.Should().Be("USD");
    }

    [Fact]
    public async Task RegisterAsync_CreatesDistinctApiKeys_AcrossInvocations()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var first = await service.RegisterAsync("Acme Corp", null, null);
        var second = await service.RegisterAsync("Other Corp", null, null);

        first.PlaintextApiKey.Should().NotBe(second.PlaintextApiKey);
        first.Tenant.ApiKeyHash.Should().NotBe(second.Tenant.ApiKeyHash);
    }

    [Fact]
    public async Task RegisterAsync_NewTenantIsActive()
    {
        var repository = Substitute.For<ITenantRepository>();
        var service = new TenantService(repository);

        var result = await service.RegisterAsync("Acme Corp", null, null);

        result.Tenant.IsActive.Should().BeTrue();
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
