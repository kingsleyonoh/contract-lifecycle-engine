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
/// DI wiring tests for every Phase 3 ecosystem client feature flag:
/// Notification Hub, Workflow Engine, Compliance Ledger (NATS), Invoice Recon.
///
/// <para>Each flag has identical semantics: ENABLED=true + URL present → real client;
/// ENABLED=false OR missing → no-op stub. Missing URL while ENABLED=true MUST throw at DI build
/// time so misconfigured deployments surface the error immediately rather than on first dispatch.
/// The Compliance Ledger variant validates NATS_URL instead of a *_URL variable.</para>
/// </summary>
public class EcosystemClientDiTests
{
    // ---------- Notification Hub ----------

    [Fact]
    public void NotificationHub_enabled_true_and_url_present_resolves_real_client()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["NOTIFICATION_HUB_ENABLED"] = "true",
            ["NOTIFICATION_HUB_URL"] = "https://hub.kingsleyonoh.com",
            ["NOTIFICATION_HUB_API_KEY"] = "test-key",
        });

        using var provider = BuildProvider(config);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
        client.Should().BeOfType<NotificationHubClient>();
    }

    [Fact]
    public void NotificationHub_enabled_false_resolves_noop_stub()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["NOTIFICATION_HUB_ENABLED"] = "false",
        });

        using var provider = BuildProvider(config);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
        client.Should().BeOfType<NoOpNotificationPublisher>();
    }

    [Fact]
    public void NotificationHub_flag_absent_defaults_to_noop_stub()
    {
        using var provider = BuildProvider(BuildConfig(new Dictionary<string, string?>()));
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
        client.Should().BeOfType<NoOpNotificationPublisher>();
    }

    [Fact]
    public void NotificationHub_enabled_true_without_url_throws_at_DI_build_time()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["NOTIFICATION_HUB_ENABLED"] = "true",
        });

        var act = () => BuildProvider(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOTIFICATION_HUB_URL is required*");
    }

    // ---------- Workflow Engine ----------

    [Fact]
    public void WorkflowEngine_enabled_true_and_url_present_resolves_real_client()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WORKFLOW_ENGINE_ENABLED"] = "true",
            ["WORKFLOW_ENGINE_URL"] = "https://workflows.kingsleyonoh.com",
            ["WORKFLOW_ENGINE_API_KEY"] = "test-key",
        });

        using var provider = BuildProvider(config);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkflowTrigger>();
        client.Should().BeOfType<WorkflowEngineClient>();
    }

    [Fact]
    public void WorkflowEngine_flag_absent_defaults_to_noop_stub()
    {
        using var provider = BuildProvider(BuildConfig(new Dictionary<string, string?>()));
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkflowTrigger>();
        client.Should().BeOfType<NoOpWorkflowTrigger>();
    }

    [Fact]
    public void WorkflowEngine_enabled_true_without_url_throws_at_DI_build_time()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["WORKFLOW_ENGINE_ENABLED"] = "true",
        });

        var act = () => BuildProvider(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WORKFLOW_ENGINE_URL is required*");
    }

    // ---------- Invoice Recon ----------

    [Fact]
    public void InvoiceRecon_enabled_true_and_url_present_resolves_real_client()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["INVOICE_RECON_ENABLED"] = "true",
            ["INVOICE_RECON_URL"] = "https://invoices.kingsleyonoh.com",
            ["INVOICE_RECON_API_KEY"] = "test-key",
        });

        using var provider = BuildProvider(config);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvoiceReconClient>();
        client.Should().BeOfType<InvoiceReconClient>();
    }

    [Fact]
    public void InvoiceRecon_flag_absent_defaults_to_noop_stub()
    {
        using var provider = BuildProvider(BuildConfig(new Dictionary<string, string?>()));
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvoiceReconClient>();
        client.Should().BeOfType<NoOpInvoiceReconClient>();
    }

    [Fact]
    public void InvoiceRecon_enabled_true_without_url_throws_at_DI_build_time()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["INVOICE_RECON_ENABLED"] = "true",
        });

        var act = () => BuildProvider(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*INVOICE_RECON_URL is required*");
    }

    // ---------- Compliance Ledger (NATS) ----------

    [Fact]
    public void ComplianceLedger_enabled_false_resolves_noop_stub()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["COMPLIANCE_LEDGER_ENABLED"] = "false",
        });

        using var provider = BuildProvider(config);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IComplianceEventPublisher>();
        client.Should().BeOfType<NoOpCompliancePublisher>();
    }

    [Fact]
    public void ComplianceLedger_flag_absent_defaults_to_noop_stub()
    {
        using var provider = BuildProvider(BuildConfig(new Dictionary<string, string?>()));
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IComplianceEventPublisher>();
        client.Should().BeOfType<NoOpCompliancePublisher>();
    }

    [Fact]
    public void ComplianceLedger_enabled_true_without_nats_url_throws_at_DI_build_time()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["COMPLIANCE_LEDGER_ENABLED"] = "true",
        });

        var act = () => BuildProvider(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NATS_URL is required*");
    }

    // ---------- helpers ----------

    private static ServiceProvider BuildProvider(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddContractEngineInfrastructure(config);
        return services.BuildServiceProvider();
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
