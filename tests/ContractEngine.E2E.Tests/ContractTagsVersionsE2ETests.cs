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
/// Boots the compiled API DLL over Kestrel and drives the tag + version flows end-to-end:
/// register → contract → POST tags → POST version → GET versions. Asserts version_number starts
/// at 2 and the returned tag list matches the request (after dedupe / normalisation). Catches
/// middleware / rate-limiter / form-binding issues that WebApplicationFactory tests cannot.
/// </summary>
public class ContractTagsVersionsE2ETests : IAsyncLifetime
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
            Timeout = TimeSpan.FromSeconds(15),
        };
        await WaitForHealthyAsync(_client, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task TagsAndVersions_RoundTripThroughRealKestrel()
    {
        // 1. register tenant
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E Tags-Versions {Guid.NewGuid()}",
        });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var regDoc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = regDoc.RootElement.GetProperty("apiKey").GetString()!;

        using var authed = new HttpClient
        {
            BaseAddress = _client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(15),
        };
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);

        // 2. counterparty + contract
        var cpResp = await authed.PostAsJsonAsync("/api/counterparties", new { name = $"E2E-TV-CP {Guid.NewGuid()}" });
        cpResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var cpDoc = JsonDocument.Parse(await cpResp.Content.ReadAsStringAsync());
        var cpId = cpDoc.RootElement.GetProperty("id").GetGuid();

        var contractResp = await authed.PostAsJsonAsync("/api/contracts", new
        {
            title = $"E2E-TV-Contract {Guid.NewGuid()}",
            counterparty_id = cpId,
            contract_type = "vendor",
        });
        contractResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var contractDoc = JsonDocument.Parse(await contractResp.Content.ReadAsStringAsync());
        var contractId = contractDoc.RootElement.GetProperty("id").GetGuid();

        // 3. POST tags (with duplicates — must dedupe)
        var tagResp = await authed.PostAsJsonAsync($"/api/contracts/{contractId}/tags", new
        {
            tags = new[] { "e2e", "real-kestrel", "e2e" },
        });
        tagResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var tagDoc = JsonDocument.Parse(await tagResp.Content.ReadAsStringAsync());
        tagDoc.RootElement.GetProperty("tags").GetArrayLength().Should().Be(2);

        // 4. POST version → must produce version_number=2 (contract seeded with current_version=1)
        var versionResp = await authed.PostAsJsonAsync($"/api/contracts/{contractId}/versions", new
        {
            change_summary = "E2E first amendment",
            created_by = "e2e-runner",
        });
        versionResp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var versionDoc = JsonDocument.Parse(await versionResp.Content.ReadAsStringAsync());
        versionDoc.RootElement.GetProperty("version_number").GetInt32().Should().Be(2);

        // 5. GET versions — must report the newly-created version in the data[] array
        var listResp = await authed.GetAsync($"/api/contracts/{contractId}/versions");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        listDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
        listDoc.RootElement.GetProperty("pagination").GetProperty("total_count").GetInt64().Should().Be(1);
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
