using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ContractEngine.E2E.Tests;

/// <summary>
/// Boots the compiled Api DLL over Kestrel on dedicated port 5056 and exercises the Batch 013
/// recurring-fulfill cascade end-to-end: register → counterparty → contract → create monthly
/// obligation → confirm → fulfill → GET list → assert two rows (Fulfilled parent + new Active
/// child with next_due_date advanced by one month). Complements
/// <see cref="ObligationEndpointsE2ETests"/> which covers Pending-state transitions.
/// </summary>
public class ObligationLifecycleE2ETests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private readonly StringBuilder _stderrCapture = new();
    private const int Port = 5056;
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
    public async Task MonthlyRecurrenceFulfillSpawnsFollowUp_RoundTripsThroughRealKestrel()
    {
        // Register tenant.
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E Recur {Guid.NewGuid()}",
        });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var regDoc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = regDoc.RootElement.GetProperty("apiKey").GetString()!;

        using var authed = new HttpClient
        {
            BaseAddress = _client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        // Counterparty.
        var cpResp = await authed.PostAsJsonAsync("/api/counterparties", new
        {
            name = $"E2E-Recur-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        // Contract.
        var contractResp = await authed.PostAsJsonAsync("/api/contracts", new
        {
            title = $"E2E Recur Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        contractResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var contractDoc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        var contractId = contractDoc.RootElement.GetProperty("id").GetGuid();

        // Monthly recurring obligation.
        var oblResp = await authed.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = "Monthly SaaS fee",
            deadline_date = "2026-04-16",
            recurrence = "monthly",
            amount = 1200m,
            currency = "USD",
        });
        oblResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var oblDoc = JsonDocument.Parse(await oblResp.Content.ReadAsStringAsync());
        var parentId = oblDoc.RootElement.GetProperty("id").GetGuid();

        // Confirm → Active.
        (await authed.PostAsJsonAsync($"/api/obligations/{parentId}/confirm", new { }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Fulfill — should spawn child.
        var fulfillResp = await authed.PostAsJsonAsync($"/api/obligations/{parentId}/fulfill", new
        {
            notes = "ACH ref 99887",
        });
        fulfillResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET list for this contract — expect two rows.
        var listResp = await authed.GetAsync($"/api/obligations?contract_id={contractId}");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items = listDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Count.Should().Be(2);

        // One is the Fulfilled parent; the other is an Active child with next_due_date 2026-05-16.
        var parent = items.Single(x => x.GetProperty("id").GetGuid() == parentId);
        parent.GetProperty("status").GetString().Should().Be("fulfilled");

        var child = items.Single(x => x.GetProperty("id").GetGuid() != parentId);
        child.GetProperty("status").GetString().Should().Be("active");
        child.GetProperty("next_due_date").GetString().Should().Be("2026-05-16");
        child.GetProperty("recurrence").GetString().Should().Be("monthly");
        child.GetProperty("amount").GetDecimal().Should().Be(1200m);
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
