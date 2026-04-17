using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ContractEngine.E2E.Tests;

/// <summary>
/// E2E tests for <c>POST /api/webhooks/contract-signed</c> against a real compiled subprocess.
/// Catches middleware-ordering / rate-limiter / HMAC-verification wiring issues that the
/// in-process WebApplicationFactory tests miss.
///
/// <para>Port 5062 — next unused port above the Phase 1/2 range (5050–5061 were taken as of Batch
/// 022). The signing secret is injected via <c>WEBHOOK_SIGNING_SECRET</c> env var; the
/// <c>WEBHOOK_ENGINE_ENABLED=true</c> flag turns the endpoint on.</para>
/// </summary>
public class WebhookEndpointE2ETests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private readonly StringBuilder _stderrCapture = new();
    private const int Port = 5062;
    private const string SigningSecret = "cle_test_webhook_e2e_2026";
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
        psi.Environment["WEBHOOK_ENGINE_ENABLED"] = "true";
        psi.Environment["WEBHOOK_SIGNING_SECRET"] = SigningSecret;

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

    [Fact]
    public async Task Post_WithValidHmacAndTenantId_Returns202_OverRealKestrel()
    {
        // Register a tenant via the public endpoint — same pattern the other E2E tests use.
        var reg = await _client!.PostAsJsonAsync("/api/tenants/register", new { name = $"WebhookE2E {Guid.NewGuid()}" });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        using var regDoc = JsonDocument.Parse(await reg.Content.ReadAsStringAsync());
        var tenantId = regDoc.RootElement.GetProperty("id").GetGuid();

        var envelopeId = $"env-e2e-{Guid.NewGuid():N}";
        var body = $$"""
        {
          "event": "envelope.completed",
          "envelope_id": "{{envelopeId}}",
          "envelope_name": "E2E Signed Contract",
          "documents": [ { "name": "e2e.pdf", "download_url": "https://example.invalid/will-fail.pdf" } ]
        }
        """;
        var signature = ComputeHmac(body, SigningSecret);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/contract-signed?source=docusign")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
        req.Headers.Add("X-Tenant-Id", tenantId.ToString());

        var resp = await _client.SendAsync(req);

        // Even if the document download fails (download URL is invalid), the endpoint still acks
        // 202 — the webhook was accepted; background retry policy handles the download failure.
        // The contract row should still be created (Draft status) for humans to review.
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Post_WithMismatchedHmac_Returns401_OverRealKestrel()
    {
        var body = """{"event":"envelope.completed","envelope_id":"env-bad-sig"}""";
        var wrongSig = ComputeHmac(body, "wrong-secret");
        var reg = await _client!.PostAsJsonAsync("/api/tenants/register", new { name = $"Bad {Guid.NewGuid()}" });
        using var regDoc = JsonDocument.Parse(await reg.Content.ReadAsStringAsync());
        var tenantId = regDoc.RootElement.GetProperty("id").GetGuid();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/contract-signed?source=docusign")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Webhook-Signature", $"sha256={wrongSig}");
        req.Headers.Add("X-Tenant-Id", tenantId.ToString());

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string ComputeHmac(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
