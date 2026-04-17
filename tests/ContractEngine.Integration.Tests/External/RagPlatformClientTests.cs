using System.Net;
using ContractEngine.Core.Integrations.Rag;
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
/// Integration tests for the real <see cref="RagPlatformClient"/> against a WireMock.Net fake
/// server. This is an allowed mock per project policy — the RAG Platform is an EXTERNAL service
/// we do not own (mock policy in CODING_STANDARDS_TESTING_LIVE.md §"Don't Mock What You Own").
///
/// <para>Each test spins up a fresh WireMock server on an auto-assigned port, registers the
/// expected stub(s), then drives the client through <c>IHttpClientFactory</c> so the resilience
/// pipeline (retry + circuit breaker) is wired the same way Production would wire it.</para>
/// </summary>
public class RagPlatformClientTests : IAsyncLifetime
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

    private IRagPlatformClient BuildClient(string apiKey = "test-key")
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RAG_PLATFORM_API_KEY"] = apiKey,
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(NullLogger<RagPlatformClient>.Instance);
        services.AddHttpClient<IRagPlatformClient, RagPlatformClient>(client =>
        {
            client.BaseAddress = new Uri(_server.Url!);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<IRagPlatformClient>();
    }

    [Fact]
    public async Task UploadDocumentAsync_sends_multipart_and_parses_json_response()
    {
        _server
            .Given(Request.Create().WithPath("/api/documents").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":"doc-42","file_name":"contract.pdf","status":"indexed"}"""));

        var client = BuildClient();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await client.UploadDocumentAsync(stream, "contract.pdf", "application/pdf");

        result.Id.Should().Be("doc-42");
        result.FileName.Should().Be("contract.pdf");
        result.Status.Should().Be("indexed");

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        log.RequestMessage.Method.Should().Be("POST");
        log.RequestMessage.Headers!["X-API-Key"].Should().Contain("test-key");
        log.RequestMessage.Headers!["Content-Type"].Single().Should().Contain("multipart/form-data");
    }

    [Fact]
    public async Task SearchAsync_posts_json_and_parses_hits()
    {
        _server
            .Given(Request.Create().WithPath("/api/search").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"hits":[{"document_id":"doc-1","chunk":"net 30","score":0.91}]}"""));

        var client = BuildClient();
        var result = await client.SearchAsync(
            "payment terms",
            new Dictionary<string, object> { ["document_id"] = "doc-1" });

        result.Hits.Should().HaveCount(1);
        result.Hits[0].DocumentId.Should().Be("doc-1");
        result.Hits[0].Chunk.Should().Be("net 30");
        result.Hits[0].Score.Should().BeApproximately(0.91, 0.001);
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_no_hits()
    {
        _server
            .Given(Request.Create().WithPath("/api/search").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"hits":[]}"""));

        var client = BuildClient();
        var result = await client.SearchAsync("query", filters: null);

        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatSyncAsync_posts_json_body_and_parses_answer_plus_sources()
    {
        _server
            .Given(Request.Create().WithPath("/api/chat/sync").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "answer": "{\"net_days\":30}",
                  "sources": [{"document_id":"doc-1","chunk":"Net 30 days"}]
                }
                """));

        var client = BuildClient();
        var result = await client.ChatSyncAsync(
            "extract payment terms",
            filters: null,
            responseFormat: "json");

        result.Answer.Should().Contain("net_days");
        result.Sources.Should().HaveCount(1);
        result.Sources[0].DocumentId.Should().Be("doc-1");
        result.Sources[0].Chunk.Should().Be("Net 30 days");
    }

    [Fact]
    public async Task GetEntitiesAsync_sends_GET_with_query_string_and_parses_array()
    {
        _server
            .Given(Request.Create().WithPath("/api/entities").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "entities": [
                    {"type":"party","value":"Acme Corp","confidence":0.98},
                    {"type":"amount","value":"$10,000","confidence":0.87}
                  ]
                }
                """));

        var client = BuildClient();
        var entities = await client.GetEntitiesAsync("doc-1");

        entities.Should().HaveCount(2);
        entities[0].Type.Should().Be("party");
        entities[0].Value.Should().Be("Acme Corp");
        entities[0].Confidence.Should().BeApproximately(0.98, 0.001);

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        log.RequestMessage.Url.Should().Contain("document_id=doc-1");
    }

    [Fact]
    public async Task Non_success_status_throws_RagPlatformException_with_status_code()
    {
        _server
            .Given(Request.Create().WithPath("/api/search").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode((int)HttpStatusCode.BadRequest)
                .WithBody("bad query"));

        var client = BuildClient();

        var act = async () => await client.SearchAsync("q", filters: null);

        var ex = (await act.Should().ThrowAsync<RagPlatformException>()).Subject.Single();
        ex.StatusCode.Should().Be(400);
        ex.ResponseBody.Should().Be("bad query");
    }

    [Fact]
    public async Task Api_key_header_is_added_on_every_request()
    {
        _server
            .Given(Request.Create().WithPath("/api/entities").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"entities":[]}"""));

        var client = BuildClient(apiKey: "cle_rag_test_12345");
        await client.GetEntitiesAsync("doc-1");

        var log = _server.LogEntries.Should().ContainSingle().Subject;
        log.RequestMessage.Headers!["X-API-Key"].Should().Contain("cle_rag_test_12345");
    }
}
