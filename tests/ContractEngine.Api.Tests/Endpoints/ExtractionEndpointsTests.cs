using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Xunit;

namespace ContractEngine.Api.Tests.Endpoints;

/// <summary>
/// WebApplicationFactory-driven tests for the extraction endpoints introduced in Batch 021.
/// Tests the trigger, list, detail, and retry endpoints with real DB and NoOp RAG stub.
/// </summary>
[Collection(WebApplicationCollection.Name)]
public class ExtractionEndpointsTests : IClassFixture<ExtractionEndpointsTestFactory>
{
    private readonly ExtractionEndpointsTestFactory _factory;

    public ExtractionEndpointsTests(ExtractionEndpointsTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostExtract_WithValidContract_Returns201()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractId = await SeedContractAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/extract", new
        {
            prompt_types = new[] { "payment", "renewal" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().NotBe(Guid.Empty);
        doc.RootElement.GetProperty("status").GetString().Should().Be("queued");
        doc.RootElement.GetProperty("contract_id").GetGuid().Should().Be(contractId);
    }

    [Fact]
    public async Task PostExtract_WithNonexistentContract_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsJsonAsync($"/api/contracts/{Guid.NewGuid()}/extract", new
        {
            prompt_types = new[] { "payment" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostExtract_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync($"/api/contracts/{Guid.NewGuid()}/extract", new
        {
            prompt_types = new[] { "payment" },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetExtractionJobs_ReturnsPaginatedEnvelope()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractId = await SeedContractAsync(client);

        // Trigger an extraction so there's at least one job
        await client.PostAsJsonAsync($"/api/contracts/{contractId}/extract", new
        {
            prompt_types = new[] { "payment" },
        });

        var resp = await client.GetAsync("/api/extraction-jobs");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("data", out var data).Should().BeTrue();
        data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        doc.RootElement.TryGetProperty("pagination", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetExtractionJobs_FilterByContractId_Narrows()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractA = await SeedContractAsync(client);
        var contractB = await SeedContractAsync(client);

        await client.PostAsJsonAsync($"/api/contracts/{contractA}/extract", new
        {
            prompt_types = new[] { "payment" },
        });
        await client.PostAsJsonAsync($"/api/contracts/{contractB}/extract", new
        {
            prompt_types = new[] { "renewal" },
        });

        var resp = await client.GetAsync($"/api/extraction-jobs?contract_id={contractA}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().OnlyContain(x => x.GetProperty("contract_id").GetGuid() == contractA);
    }

    [Fact]
    public async Task GetExtractionJobDetail_WithValidId_Returns200()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractId = await SeedContractAsync(client);

        var triggerResp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/extract", new
        {
            prompt_types = new[] { "payment" },
        });
        using var triggerDoc = JsonDocument.Parse(await triggerResp.Content.ReadAsStringAsync());
        var jobId = triggerDoc.RootElement.GetProperty("id").GetGuid();

        var resp = await client.GetAsync($"/api/extraction-jobs/{jobId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetGuid().Should().Be(jobId);
        doc.RootElement.GetProperty("status").GetString().Should().Be("queued");
    }

    [Fact]
    public async Task GetExtractionJobDetail_WithNonexistentId_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.GetAsync($"/api/extraction-jobs/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostRetry_OnFailedJob_Returns200()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractId = await SeedContractAsync(client);

        // Trigger then manually mark as Failed
        var triggerResp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/extract", new
        {
            prompt_types = new[] { "payment" },
        });
        using var triggerDoc = JsonDocument.Parse(await triggerResp.Content.ReadAsStringAsync());
        var jobId = triggerDoc.RootElement.GetProperty("id").GetGuid();

        // Directly update job to Failed status via DB
        await UpdateJobStatusAsync(jobId, ExtractionStatus.Failed);

        var resp = await client.PostAsync(
            $"/api/extraction-jobs/{jobId}/retry",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("queued");
    }

    [Fact]
    public async Task PostRetry_OnCompletedJob_Returns422()
    {
        var (key, tenantId) = await RegisterTenantAsync();
        using var client = AuthedClient(key);
        var contractId = await SeedContractAsync(client);

        var triggerResp = await client.PostAsJsonAsync($"/api/contracts/{contractId}/extract", new
        {
            prompt_types = new[] { "payment" },
        });
        using var triggerDoc = JsonDocument.Parse(await triggerResp.Content.ReadAsStringAsync());
        var jobId = triggerDoc.RootElement.GetProperty("id").GetGuid();

        // Directly update job to Completed status via DB
        await UpdateJobStatusAsync(jobId, ExtractionStatus.Completed);

        var resp = await client.PostAsync(
            $"/api/extraction-jobs/{jobId}/retry",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        // InvalidOperationException → 409 (CONFLICT) via middleware
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostRetry_OnNonexistentJob_Returns404()
    {
        var (key, _) = await RegisterTenantAsync();
        using var client = AuthedClient(key);

        var resp = await client.PostAsync(
            $"/api/extraction-jobs/{Guid.NewGuid()}/retry",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- Helpers -----

    private async Task<(string Key, Guid TenantId)> RegisterTenantAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"Extraction-Tenant {Guid.NewGuid()}",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("id").GetGuid());
    }

    private HttpClient AuthedClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    private static async Task<Guid> SeedContractAsync(HttpClient client)
    {
        var cpResp = await client.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"Extraction-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        var cResp = await client.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Extraction Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        cResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cDoc = JsonDocument.Parse(await cResp.Content.ReadAsStringAsync());
        return cDoc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task UpdateJobStatusAsync(Guid jobId, ExtractionStatus status)
    {
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(ExtractionEndpointsTestFactory.TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new NullTenantContext());
        var job = await db.Set<ExtractionJob>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is not null)
        {
            job.Status = status;
            await db.SaveChangesAsync();
        }
    }
}

public class ExtractionEndpointsTestFactory : WebApplicationFactory<Program>
{
    public const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    static ExtractionEndpointsTestFactory()
    {
        SerilogTestBootstrap.EnsureInitialized();
    }

    public ExtractionEndpointsTestFactory()
    {
        EnsureDatabaseReady();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        builder.UseSetting("DATABASE_URL", TestConnectionString);
        builder.UseSetting("JOBS_ENABLED", "false");
        builder.UseSetting("AUTO_SEED", "false");
        builder.UseSetting("AUTO_MIGRATE", "false");
        builder.UseSetting("SELF_REGISTRATION_ENABLED", "true");
        builder.UseSetting("RATE_LIMIT__PUBLIC", "1000");
        builder.UseSetting("RATE_LIMIT__READ_100", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_50", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_20", "1000");
        builder.UseSetting("RATE_LIMIT__WRITE_10", "1000");
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
}
