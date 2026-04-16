using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ContractEngine.E2E.Tests;

public class HealthEndpointTests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private StringBuilder _stderrCapture = new();
    private const int Port = 5050;
    private const string TestConnectionString =
        "Host=localhost;Port=5445;Database=contract_engine_test;Username=contract_engine;Password=localdev";

    public async Task InitializeAsync()
    {
        _serverProcess = StartApiSubprocess(Port, _stderrCapture);
        await WaitForPortBoundAsync(_serverProcess, Port, TimeSpan.FromSeconds(45), _stderrCapture);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(10) };
        await WaitForHealthyAsync(_client, TimeSpan.FromSeconds(20));
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
                throw new InvalidOperationException($"E2E server exited prematurely (code {process.ExitCode}). Stderr: {stderrCapture}");
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

        throw new InvalidOperationException($"E2E server bound the port but /health never returned 200 within {timeout.TotalSeconds} seconds");
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client!.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }

    [Fact]
    public async Task TenantRegistration_ThenApiKeyAuthentication_RoundTripsThroughRealKestrel()
    {
        // Step 1: register a fresh tenant via the public endpoint.
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new
        {
            name = $"E2E {Guid.NewGuid()}",
        });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var doc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = doc.RootElement.GetProperty("apiKey").GetString();
        apiKey.Should().NotBeNullOrWhiteSpace();
        apiKey.Should().StartWith("cle_live_");

        // Step 2: use the returned key to hit /health (a public endpoint that still flows through
        // TenantResolutionMiddleware). A 200 confirms the middleware accepts the key's hash
        // against the row just inserted.
        using var authed = new HttpClient
        {
            BaseAddress = _client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        var healthResp = await authed.GetAsync("/health");
        healthResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTenantsMe_WithValidKey_ReturnsTenantShape_OverRealKestrel()
    {
        // Register a tenant through the live server, then round-trip the authenticated GET /me
        // endpoint to confirm the rate-limiter middleware, tenant resolution middleware, and
        // endpoint handler all cooperate once the service is running out of the compiled DLL
        // (catches wiring issues that in-process test clients miss — middleware order, route
        // mapping, and RateLimiter/exception-handler interaction).
        var uniqueName = $"E2E Me {Guid.NewGuid()}";
        var registerResp = await _client!.PostAsJsonAsync("/api/tenants/register", new { name = uniqueName });
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var regDoc = JsonDocument.Parse(await registerResp.Content.ReadAsStringAsync());
        var apiKey = regDoc.RootElement.GetProperty("apiKey").GetString()!;
        var expectedId = regDoc.RootElement.GetProperty("id").GetGuid();

        using var authed = new HttpClient
        {
            BaseAddress = _client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        authed.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        var meResp = await authed.GetAsync("/api/tenants/me");
        meResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var meDoc = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync());
        var root = meDoc.RootElement;
        root.GetProperty("id").GetGuid().Should().Be(expectedId);
        root.GetProperty("name").GetString().Should().Be(uniqueName);
        root.GetProperty("default_timezone").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("default_currency").GetString().Should().NotBeNullOrEmpty();
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
