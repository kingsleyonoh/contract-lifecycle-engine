using System.Net;
using System.Text;
using ContractEngine.Core.Integrations.Webhooks;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.External;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Integration tests for <see cref="WebhookDocumentDownloader"/> against a WireMock fake server.
/// The downloader streams signed-contract bytes from DocuSign/PandaDoc URLs that the webhook
/// payload pointed us at. Authentication is out of scope — the payload itself contains a signed /
/// time-limited URL so no per-request headers are required by default.
///
/// <para>Non-success HTTP status codes MUST bubble up as <see cref="WebhookDownloadException"/> so
/// the endpoint handler can roll back the draft contract create. No retries at this layer — the
/// HTTP client's own resilience pipeline handles those.</para>
/// </summary>
public class WebhookDocumentDownloaderTests : IAsyncLifetime
{
    private WireMockServer _server = default!;
    private ServiceProvider _provider = default!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _provider?.Dispose();
        _server?.Stop();
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private IWebhookDocumentDownloader BuildDownloader()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(NullLogger<WebhookDocumentDownloader>.Instance);
        services.AddHttpClient<IWebhookDocumentDownloader, WebhookDocumentDownloader>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<IWebhookDocumentDownloader>();
    }

    [Fact]
    public async Task Download_200_StreamsBytes()
    {
        var content = "the signed pdf bytes";
        _server
            .Given(Request.Create().WithPath("/file.pdf").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/pdf")
                .WithBody(Encoding.UTF8.GetBytes(content)));

        var downloader = BuildDownloader();
        await using var stream = await downloader.DownloadAsync($"{_server.Url}/file.pdf");

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Be(content);
    }

    [Fact]
    public async Task Download_404_ThrowsWebhookDownloadException()
    {
        _server
            .Given(Request.Create().WithPath("/missing.pdf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.NotFound));

        var downloader = BuildDownloader();
        var act = async () => await downloader.DownloadAsync($"{_server.Url}/missing.pdf");

        var ex = (await act.Should().ThrowAsync<WebhookDownloadException>()).Subject.Single();
        ex.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Download_500_ThrowsWebhookDownloadException()
    {
        _server
            .Given(Request.Create().WithPath("/brokenserver.pdf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.InternalServerError));

        var downloader = BuildDownloader();
        var act = async () => await downloader.DownloadAsync($"{_server.Url}/brokenserver.pdf");

        var ex = (await act.Should().ThrowAsync<WebhookDownloadException>()).Subject.Single();
        ex.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Download_EmptyUrl_ThrowsArgumentException()
    {
        var downloader = BuildDownloader();
        var act = async () => await downloader.DownloadAsync("");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
