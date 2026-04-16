using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Configuration;
using ContractEngine.Infrastructure.External;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// DI wiring tests for the RAG Platform feature flag. Proves that
/// <see cref="ServiceRegistration.AddContractEngineInfrastructure"/> resolves the real
/// <see cref="RagPlatformClient"/> when <c>RAG_PLATFORM_ENABLED=true</c> and the
/// <see cref="NoOpRagPlatformClient"/> stub when the flag is absent or <c>false</c>.
///
/// <para>We build a lightweight provider rather than using <c>DatabaseFixture</c> — these tests
/// don't need a live Postgres, just the registration delegate.</para>
/// </summary>
public class RagPlatformClientDiTests
{
    [Fact]
    public void When_enabled_true_and_url_present_resolves_real_client()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["RAG_PLATFORM_ENABLED"] = "true",
            ["RAG_PLATFORM_URL"] = "https://ai.kingsleyonoh.com",
            ["RAG_PLATFORM_API_KEY"] = "test-key",
        });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddContractEngineInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IRagPlatformClient>();

        client.Should().BeOfType<RagPlatformClient>();
    }

    [Fact]
    public void When_enabled_false_resolves_noop_stub()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["RAG_PLATFORM_ENABLED"] = "false",
        });

        var services = new ServiceCollection();
        services.AddContractEngineInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IRagPlatformClient>();

        client.Should().BeOfType<NoOpRagPlatformClient>();
    }

    [Fact]
    public void When_flag_absent_defaults_to_noop_stub()
    {
        // Default behaviour: RAG is disabled unless explicitly enabled. Anyone deploying without
        // the full stack (local dev, CI) should never hit the real HTTP client by accident.
        var config = BuildConfig(new Dictionary<string, string?>());

        var services = new ServiceCollection();
        services.AddContractEngineInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IRagPlatformClient>();

        client.Should().BeOfType<NoOpRagPlatformClient>();
    }

    [Fact]
    public void When_enabled_true_without_url_throws_at_DI_build_time()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["RAG_PLATFORM_ENABLED"] = "true",
        });

        var services = new ServiceCollection();
        var act = () => services.AddContractEngineInfrastructure(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RAG_PLATFORM_URL is required*");
    }

    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
    {
        // Provide a DATABASE_URL so the rest of the Infrastructure registration works; it
        // doesn't need to point at a live DB for DI resolution.
        values["DATABASE_URL"] =
            "Host=localhost;Port=5445;Database=contract_engine;Username=contract_engine;Password=localdev";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
