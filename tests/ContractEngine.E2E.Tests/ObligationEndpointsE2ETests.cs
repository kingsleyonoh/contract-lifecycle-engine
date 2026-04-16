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
/// Boots the compiled Api DLL over Kestrel on a dedicated port (5055) and exercises the
/// Batch 012 obligation flow end-to-end: register tenant → create counterparty → create
/// contract → POST obligation → POST /confirm → GET detail → assert status=active and
/// events.count=1. Complements <see cref="ContractLifecycleE2ETests"/>; covers middleware
/// ordering, rate limiter partitioning, and the Pending→Active transition through real HTTP.
/// </summary>
public class ObligationEndpointsE2ETests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private readonly StringBuilder _stderrCapture = new();
    private const int Port = 5055;
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
    public async Task CreateConfirmAndReadDetail_RoundTripsThroughRealKestrel()
    {
        // Register tenant.
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E Obligation {Guid.NewGuid()}",
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
            name = $"E2E-Obligation-CP {Guid.NewGuid()}",
        });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        // Contract.
        var contractResp = await authed.PostAsJsonAsync("/api/contracts", new
        {
            title = $"E2E Obligation Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        contractResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var contractDoc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        var contractId = contractDoc.RootElement.GetProperty("id").GetGuid();

        // Obligation.
        var oblResp = await authed.PostAsJsonAsync("/api/obligations", new
        {
            contract_id = contractId,
            obligation_type = "payment",
            title = "NET-30 license",
            deadline_date = "2026-10-01",
        });
        oblResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var oblDoc = JsonDocument.Parse(await oblResp.Content.ReadAsStringAsync());
        var oblId = oblDoc.RootElement.GetProperty("id").GetGuid();
        oblDoc.RootElement.GetProperty("status").GetString().Should().Be("pending");

        // Confirm.
        var confirmResp = await authed.PostAsJsonAsync($"/api/obligations/{oblId}/confirm", new { });
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET detail + assert status=active + events count=1.
        var detailResp = await authed.GetAsync($"/api/obligations/{oblId}");
        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync());
        detailDoc.RootElement.GetProperty("status").GetString().Should().Be("active");
        var events = detailDoc.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(1);
        events[0].GetProperty("from_status").GetString().Should().Be("pending");
        events[0].GetProperty("to_status").GetString().Should().Be("active");
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
