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
/// Boots the compiled Api DLL over Kestrel on a dedicated port and exercises the contract CRUD
/// round-trip: register tenant → create counterparty → create contract referencing it → list
/// contracts → assert the new contract appears. Catches wiring issues missed by
/// WebApplicationFactory tests (middleware order, DI, rate-limiter policies, JSON enum naming).
/// </summary>
public class ContractEndpointsE2ETests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private readonly StringBuilder _stderrCapture = new();
    private const int Port = 5052;
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
    public async Task ContractLifecycle_RoundTripsThroughRealKestrel()
    {
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E Contract {Guid.NewGuid()}",
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

        // Step 1: create counterparty.
        var cpName = $"E2E-CP {Guid.NewGuid()}";
        var cpResp = await authed.PostAsJsonAsync("/api/counterparties", new { name = cpName });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        // Step 2: create contract referencing counterparty.
        var title = $"E2E-Contract {Guid.NewGuid()}";
        var contractResp = await authed.PostAsJsonAsync("/api/contracts", new
        {
            title,
            counterparty_id = cpId,
            contract_type = "vendor",
            effective_date = "2026-05-01",
            end_date = "2027-05-01",
        });
        contractResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var contractDoc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        var contractId = contractDoc.RootElement.GetProperty("id").GetGuid();
        contractDoc.RootElement.GetProperty("status").GetString().Should().Be("draft");
        contractDoc.RootElement.GetProperty("contract_type").GetString().Should().Be("vendor");

        // Step 3: list contracts and assert the new contract is present.
        var listResp = await authed.GetAsync("/api/contracts");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var root = listDoc.RootElement;
        root.TryGetProperty("data", out var data).Should().BeTrue();
        root.TryGetProperty("pagination", out _).Should().BeTrue();

        bool found = false;
        foreach (var item in data.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == contractId)
            {
                item.GetProperty("title").GetString().Should().Be(title);
                item.GetProperty("counterparty_id").GetGuid().Should().Be(cpId);
                found = true;
                break;
            }
        }
        found.Should().BeTrue("the newly created contract must appear in the list response");
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
