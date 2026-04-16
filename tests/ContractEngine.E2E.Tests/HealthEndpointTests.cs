using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace ContractEngine.E2E.Tests;

public class HealthEndpointTests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private const int Port = 5050;

    public async Task InitializeAsync()
    {
        // Launch the compiled API exe directly from this test's output directory.
        // The .csproj references ContractEngine.Api, so the API assembly is copied next to the test binary.
        var apiDllPath = Path.Combine(AppContext.BaseDirectory, "ContractEngine.Api.dll");
        if (!File.Exists(apiDllPath))
        {
            throw new InvalidOperationException($"Expected API assembly at {apiDllPath}. Build the solution first.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{apiDllPath}\" --urls http://localhost:{Port}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{Port}";

        _serverProcess = Process.Start(psi)!;

        // Drain stdout/stderr continuously so Kestrel doesn't stall on a full redirect buffer.
        var stderrCapture = new System.Text.StringBuilder();
        _serverProcess.OutputDataReceived += (_, _) => { };
        _serverProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrCapture.AppendLine(e.Data); };
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        // Wait for the port to be listening before constructing the HttpClient.
        // Avoids spraying fetches at a port that hasn't bound yet (which otherwise
        // produces transient socket aborts on Windows).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_serverProcess.HasExited)
            {
                throw new InvalidOperationException($"E2E server exited prematurely (code {_serverProcess.ExitCode}). Stderr: {stderrCapture}");
            }

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", Port);
                if (tcp.Connected)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(10) };

        // Final confirmation — one HTTP roundtrip against the live port.
        var readyDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < readyDeadline)
        {
            try
            {
                var resp = await _client.GetAsync("/health");
                if (resp.IsSuccessStatusCode) return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException("E2E server bound the port but /health never returned 200 within 15 seconds");
    }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client!.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
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
