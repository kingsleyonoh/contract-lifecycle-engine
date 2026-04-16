using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ContractEngine.E2E.Tests;

/// <summary>
/// Boots the compiled Api DLL over Kestrel on dedicated port 5057 and exercises the Batch 015
/// alert endpoints end-to-end: register → counterparty → contract → obligation → seed an alert
/// via direct DbContext (alerts are system-generated; no public CREATE endpoint) →
/// GET /alerts → PATCH /acknowledge → GET /alerts again and verify the acknowledged state.
/// </summary>
public class AlertEndpointsE2ETests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private readonly StringBuilder _stderrCapture = new();
    private const int Port = 5057;
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    public async Task InitializeAsync()
    {
        _serverProcess = StartApiSubprocess(Port, _stderrCapture);
        await WaitForPortBoundAsync(_serverProcess, Port, TimeSpan.FromSeconds(45), _stderrCapture);
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        await WaitForHealthyAsync(_client, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Alert_ListAndAcknowledge_RoundTripsThroughRealKestrel()
    {
        // Register tenant.
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E Alerts {Guid.NewGuid()}",
        });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var regDoc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = regDoc.RootElement.GetProperty("apiKey").GetString()!;
        var tenantId = regDoc.RootElement.GetProperty("id").GetGuid();

        using var authed = new HttpClient
        {
            BaseAddress = _client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        // Counterparty + contract + obligation.
        var cpResp = await authed.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"E2E-Alert-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        var contractResp = await authed.PostAsJsonAsync("/api/contracts", new
        {
            title = $"E2E Alert Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        contractResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var contractDoc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        var contractId = contractDoc.RootElement.GetProperty("id").GetGuid();

        var oblResp = await authed.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = "Quarterly report",
            deadline_date = "2026-12-01",
        });
        oblResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var oblDoc = JsonDocument.Parse(await oblResp.Content.ReadAsStringAsync());
        var obligationId = oblDoc.RootElement.GetProperty("id").GetGuid();

        // Seed the alert directly via DbContext — no public CREATE endpoint for alerts.
        var alertId = await SeedAlertAsync(tenantId, contractId, obligationId);

        // GET /api/alerts — expect the seeded alert to appear, unacknowledged.
        var listResp = await authed.GetAsync("/api/alerts");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items = listDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().HaveCount(1);
        items[0].GetProperty("id").GetGuid().Should().Be(alertId);
        items[0].GetProperty("acknowledged").GetBoolean().Should().BeFalse();

        // PATCH /acknowledge — expect 200 + acknowledged=true in payload.
        var ackResp = await authed.PatchAsync(
            $"/api/alerts/{alertId}/acknowledge",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        ackResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ackDoc = JsonDocument.Parse(await ackResp.Content.ReadAsStringAsync());
        ackDoc.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        ackDoc.RootElement.GetProperty("acknowledged_by").GetString().Should().StartWith("user:");

        // GET /api/alerts?acknowledged=true — should include the now-acknowledged row.
        var afterResp = await authed.GetAsync("/api/alerts?acknowledged=true");
        afterResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var afterDoc = JsonDocument.Parse(await afterResp.Content.ReadAsStringAsync());
        var afterItems = afterDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        afterItems.Should().Contain(x => x.GetProperty("id").GetGuid() == alertId);
    }

    private static async Task<Guid> SeedAlertAsync(Guid tenantId, Guid contractId, Guid obligationId)
    {
        var options = new DbContextOptionsBuilder<ContractDbContext>()
            .UseNpgsql(TestConnectionString)
            .Options;
        using var db = new ContractDbContext(options, new NullTenantContext());
        var id = Guid.NewGuid();
        db.DeadlineAlerts.Add(new DeadlineAlert
        {
            Id = id,
            TenantId = tenantId,
            ContractId = contractId,
            ObligationId = obligationId,
            AlertType = AlertType.DeadlineApproaching,
            DaysRemaining = 30,
            Message = "30 days to deadline",
            Acknowledged = false,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Process StartApiSubprocess(int port, StringBuilder stderrCapture)
    {
        var apiDllPath = Path.Combine(AppContext.BaseDirectory, "ContractEngine.Api.dll");
        if (!File.Exists(apiDllPath))
        {
            throw new InvalidOperationException($"Expected API assembly at {apiDllPath}. Build the solution first.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{apiDllPath}\" --urls http://localhost:{port}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        psi.Environment["DATABASE_URL"] = TestConnectionString;
        psi.Environment["SELF_REGISTRATION_ENABLED"] = "true";
        psi.Environment["JOBS_ENABLED"] = "false";
        psi.Environment["AUTO_SEED"] = "false";

        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrCapture.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForPortBoundAsync(Process process, int port, TimeSpan timeout, StringBuilder stderrCapture)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"E2E server exited prematurely (code {process.ExitCode}). Stderr: {stderrCapture}");
            }

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port);
                if (tcp.Connected) return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }
    }

    private static async Task WaitForHealthyAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await client.GetAsync("/health");
                if (resp.IsSuccessStatusCode) return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException(
            $"E2E server bound the port but /health never returned 200 within {timeout.TotalSeconds} seconds");
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        if (_serverProcess is { HasExited: false })
        {
            try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            _serverProcess.WaitForExit(5000);
        }
        _serverProcess?.Dispose();
        return Task.CompletedTask;
    }
}
