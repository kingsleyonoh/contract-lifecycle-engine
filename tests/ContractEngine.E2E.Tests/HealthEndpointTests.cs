using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Xunit;

namespace ContractEngine.E2E.Tests;

public class HealthEndpointTests : IAsyncLifetime
{
    private Process? _serverProcess;
    private HttpClient? _client;
    private StringBuilder _stderrCapture = new();
    private const int Port = 5050;

    public async Task InitializeAsync()
    {
        _serverProcess = StartApiSubprocess(Port, _stderrCapture);
        await WaitForPortBoundAsync(_serverProcess, Port, TimeSpan.FromSeconds(30), _stderrCapture);
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(10) };
        await WaitForHealthyAsync(_client, TimeSpan.FromSeconds(15));
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
