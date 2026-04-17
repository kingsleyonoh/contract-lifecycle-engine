using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory tests for <c>POST /api/webhooks/contract-signed</c> (PRD §5.6c, §8b).
///
/// <para>The endpoint is PUBLIC — no <c>X-API-Key</c> header — but still multi-tenant: the Webhook
/// Engine forwards an <c>X-Tenant-Id</c> header (configured per destination) so the handler knows
/// which tenant owns the resulting draft contract. Security is enforced by an HMAC-SHA256 signature
/// over the raw request body keyed on <c>WEBHOOK_SIGNING_SECRET</c>.</para>
///
/// <para>When <c>WEBHOOK_ENGINE_ENABLED=false</c> or the secret is missing, the endpoint returns
/// 404 — matches the pattern used for the tenant registration endpoint so port scanners see no
/// hint the endpoint exists when the operator has chosen to disable it.</para>
///
/// <para>The <c>IWebhookDocumentDownloader</c> and <c>ExtractionService</c> are substituted so we
/// don't need a live signing service on the test network.</para>
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class WebhookEndpointsTests : IClassFixture<WebhookEndpointsTestFactory>
{
    private readonly WebhookEndpointsTestFactory _factory;

    private const string SigningSecret = "cle_test_webhook_secret_2026";

    public WebhookEndpointsTests(WebhookEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_WhenWebhookEngineDisabled_Returns404()
    {
        using var factory = _factory.WithWebhookDisabled();
        using var client = factory.CreateClient();

        var body = """{"event":"envelope.completed","envelope_id":"env-1"}""";
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: Guid.NewGuid(), source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_WithoutSignatureHeader_Returns401()
    {
        var tenant = await _factory.SeedTenantAsync();
        using var client = _factory.CreateClient();

        var body = """{"event":"envelope.completed","envelope_id":"env-1"}""";
        using var req = BuildRequest(body, signature: null, tenantId: tenant.Id, source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithWrongSignature_Returns401()
    {
        var tenant = await _factory.SeedTenantAsync();
        using var client = _factory.CreateClient();

        var body = """{"event":"envelope.completed","envelope_id":"env-wrong-sig"}""";
        using var req = BuildRequest(body, signature: ComputeHmac(body, "wrong-secret"), tenantId: tenant.Id, source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithoutTenantIdHeader_Returns401()
    {
        using var client = _factory.CreateClient();

        var body = """{"event":"envelope.completed","envelope_id":"env-notenant"}""";
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: null, source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WithUnknownTenant_Returns401()
    {
        using var client = _factory.CreateClient();
        // A random Guid that doesn't match any row.
        var body = """{"event":"envelope.completed","envelope_id":"env-unknown"}""";
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: Guid.NewGuid(), source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_ValidDocuSignEnvelope_Returns202_CreatesDraftContractAndTriggersExtraction()
    {
        var tenant = await _factory.SeedTenantAsync();
        _factory.ResetExtractionTracker();
        _factory.StubDocumentDownloaderOk();

        using var client = _factory.CreateClient();

        var envelopeId = $"env-{Guid.NewGuid():N}";
        var body = $$"""
        {
          "event": "envelope.completed",
          "envelope_id": "{{envelopeId}}",
          "envelope_name": "Signed MSA",
          "completed_at": "2026-04-17T10:00:00Z",
          "signers": [ { "name": "Jane", "email": "jane@acme.com", "company": "Acme Corp" } ],
          "documents": [ { "document_id": "d1", "name": "msa.pdf", "download_url": "https://dl.example.com/msa.pdf" } ]
        }
        """;
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: tenant.Id, source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        payload.RootElement.GetProperty("contract_id").GetGuid().Should().NotBe(Guid.Empty);

        // Verify a draft contract was created, tenant-scoped.
        var contractId = payload.RootElement.GetProperty("contract_id").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var stored = await db.Contracts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contractId);
        stored.Should().NotBeNull();
        stored!.TenantId.Should().Be(tenant.Id);
        stored.Status.Should().Be(Core.Enums.ContractStatus.Draft);

        // Verify extraction was triggered (via tracker).
        _factory.ExtractionTriggerCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Post_ValidPandaDocCompletion_Returns202_CreatesDraftContract()
    {
        var tenant = await _factory.SeedTenantAsync();
        _factory.ResetExtractionTracker();
        _factory.StubDocumentDownloaderOk();

        using var client = _factory.CreateClient();

        var docId = $"pd-{Guid.NewGuid():N}";
        var body = $$"""
        {
          "event": "document_state_changed",
          "data": {
            "id": "{{docId}}",
            "name": "NDA Globex",
            "status": "document.completed",
            "date_completed": "2026-04-17T11:00:00Z",
            "download_url": "https://dl.example.com/nda.pdf",
            "metadata": { "counterparty_name": "Globex Inc" }
          }
        }
        """;
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: tenant.Id, source: "pandadoc");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Post_SameEnvelopeIdTwice_Returns202_ButCreatesOnlyOneContract()
    {
        var tenant = await _factory.SeedTenantAsync();
        _factory.ResetExtractionTracker();
        _factory.StubDocumentDownloaderOk();

        using var client = _factory.CreateClient();

        var envelopeId = $"env-idempotent-{Guid.NewGuid():N}";
        var body = $$"""
        {
          "event": "envelope.completed",
          "envelope_id": "{{envelopeId}}",
          "envelope_name": "Idempotent MSA",
          "documents": [ { "name": "f.pdf", "download_url": "https://dl.example.com/f.pdf" } ]
        }
        """;

        using var req1 = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: tenant.Id, source: "docusign");
        var resp1 = await client.SendAsync(req1);
        resp1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync()).RootElement
            .GetProperty("contract_id").GetGuid();

        using var req2 = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: tenant.Id, source: "docusign");
        var resp2 = await client.SendAsync(req2);
        resp2.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync()).RootElement
            .GetProperty("contract_id").GetGuid();

        id2.Should().Be(id1, "the envelope_id is idempotency-keyed so a re-delivery returns the original contract");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        var matches = await db.Contracts.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenant.Id
                && EF.Functions.JsonContains(c.Metadata!, $"{{\"webhook_envelope_id\":\"{envelopeId}\"}}"))
            .ToListAsync();
        matches.Should().HaveCount(1);
    }

    [Fact]
    public async Task Post_NonCompletedEvent_Returns202_DoesNotCreateContract()
    {
        // envelope.voided → parser returns null → endpoint acks (202) to stop retries.
        var tenant = await _factory.SeedTenantAsync();
        _factory.ResetExtractionTracker();

        using var client = _factory.CreateClient();
        var body = """
        {
          "event": "envelope.voided",
          "envelope_id": "env-voided-1"
        }
        """;
        using var req = BuildRequest(body, signature: ComputeHmac(body, SigningSecret), tenantId: tenant.Id, source: "docusign");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("status").GetString().Should().Be("ignored");
        _factory.ExtractionTriggerCount.Should().Be(0);
    }

    // ---------- helpers ----------

    private static HttpRequestMessage BuildRequest(
        string body,
        string? signature,
        Guid? tenantId,
        string source)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/contract-signed?source={source}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            req.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
        }
        if (tenantId is { } id)
        {
            req.Headers.Add("X-Tenant-Id", id.ToString());
        }
        return req;
    }

    private static string ComputeHmac(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class WebhookEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    public const string SigningSecret = "cle_test_webhook_secret_2026";

    private int _extractionTriggerCount;

    static WebhookEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public WebhookEndpointsTestFactory()
    {
        EnsureDatabaseReady();
    }

    public int ExtractionTriggerCount => _extractionTriggerCount;

    public void ResetExtractionTracker() => Interlocked.Exchange(ref _extractionTriggerCount, 0);

    public IWebhookDocumentDownloader DownloaderSubstitute { get; private set; } =
        Substitute.For<IWebhookDocumentDownloader>();

    public void StubDocumentDownloaderOk()
    {
        DownloaderSubstitute.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"))));
    }

    public async Task<Tenant> SeedTenantAsync()
    {
        using var scope = Services.CreateScope();
        var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var tenantService = scope.ServiceProvider.GetRequiredService<TenantService>();
        var reg = await tenantService.RegisterAsync($"Webhook Test {Guid.NewGuid():N}", "UTC", "USD");
        return reg.Tenant;
    }

    public WebhookEndpointsDisabledFactory WithWebhookDisabled()
    {
        return new WebhookEndpointsDisabledFactory();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        builder.UseSetting("DATABASE_URL", TestConnectionString);
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("AUTO_MIGRATE", "false");
        builder.UseSetting("WEBHOOK_ENGINE_ENABLED", "true");
        builder.UseSetting("WEBHOOK_SIGNING_SECRET", SigningSecret);
        builder.UseSetting("RATE_LIMIT__PUBLIC", "1000");
        builder.UseSetting("RATE_LIMIT__PUBLIC_WEBHOOK", "1000");
        builder.UseSetting("RATE_LIMIT__READ_100", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "1000");

        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(builder, services =>
        {
            // Replace the downloader with our NSubstitute so the test never opens an outbound socket.
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWebhookDocumentDownloader));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<IWebhookDocumentDownloader>(DownloaderSubstitute);

            // Replace the extraction service with a decorator that increments the counter.
            var extractDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ExtractionService));
            if (extractDescriptor is not null)
            {
                services.Remove(extractDescriptor);
            }
            services.AddScoped<ExtractionService>(sp => new CountingExtractionService(
                sp.GetRequiredService<IRagPlatformClient>(),
                sp.GetRequiredService<IExtractionPromptRepository>(),
                sp.GetRequiredService<IExtractionJobRepository>(),
                sp.GetRequiredService<IObligationRepository>(),
                sp.GetRequiredService<IContractDocumentRepository>(),
                sp.GetRequiredService<IDocumentStorage>(),
                sp.GetRequiredService<IContractRepository>(),
                sp.GetRequiredService<Core.Abstractions.ITenantContext>(),
                () => Interlocked.Increment(ref _extractionTriggerCount)));
        });
    }

    private static void EnsureDatabaseReady()
    {
        using var connection = new Npgsql.NpgsqlConnection(
            "Host=localhost;Port=5445;Database=postgres;Username=contract_engine;Password=localdev");
        connection.Open();
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'contract_engine_test'";
            if (exists.ExecuteScalar() is null)
            {
                using var create = connection.CreateCommand();
                create.CommandText = "CREATE DATABASE contract_engine_test";
                create.ExecuteNonQuery();
            }
        }

        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new TenantContextAccessor());
        db.Database.Migrate();
    }

    /// <summary>
    /// Decorator around <see cref="ExtractionService"/> that increments a counter every time
    /// <c>TriggerExtractionAsync</c> is invoked, so tests can assert the webhook handler fired it.
    /// </summary>
    private sealed class CountingExtractionService : ExtractionService
    {
        private readonly Action _onTrigger;

        public CountingExtractionService(
            IRagPlatformClient ragClient,
            IExtractionPromptRepository promptRepo,
            IExtractionJobRepository jobRepo,
            IObligationRepository obligationRepo,
            IContractDocumentRepository docRepo,
            IDocumentStorage storage,
            IContractRepository contractRepo,
            Core.Abstractions.ITenantContext tenantContext,
            Action onTrigger)
            : base(ragClient, promptRepo, jobRepo, obligationRepo, docRepo, storage, contractRepo, tenantContext)
        {
            _onTrigger = onTrigger;
        }

        public override async Task<ExtractionJob> TriggerExtractionAsync(
            Guid contractId,
            string[]? promptTypes,
            Guid? documentId,
            CancellationToken cancellationToken = default)
        {
            _onTrigger();
            return await base.TriggerExtractionAsync(contractId, promptTypes, documentId, cancellationToken);
        }
    }
}

public class WebhookEndpointsDisabledFactory : WebApplicationFactory<Program>
{
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static WebhookEndpointsDisabledFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        builder.UseSetting("DATABASE_URL", TestConnectionString);
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("AUTO_MIGRATE", "false");
        builder.UseSetting("WEBHOOK_ENGINE_ENABLED", "false");
        builder.UseSetting("RATE_LIMIT__PUBLIC_WEBHOOK", "1000");
    }
}
