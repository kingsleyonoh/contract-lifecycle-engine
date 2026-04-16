using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Unit-level tests for <see cref="NoOpRagPlatformClient"/>. Lives in Integration.Tests (not
/// Core.Tests) because the stub is declared in the Infrastructure assembly, and Core.Tests only
/// references Core. No database or HTTP dependency — these tests run in-process and finish in
/// milliseconds, they just happen to sit in this project.
///
/// The throw-vs-empty-return split is the load-bearing contract: writes (Upload, ChatSync) MUST
/// fail loudly so extraction pipelines don't silently skip work, reads (Search, GetEntities) MUST
/// return empty so downstream UIs keep rendering when the integration is disabled.
/// </summary>
public class NoOpRagPlatformClientTests
{
    private readonly IRagPlatformClient _client = new NoOpRagPlatformClient();

    [Fact]
    public async Task SearchAsync_returns_empty_hits_without_throwing()
    {
        var result = await _client.SearchAsync("any query", filters: null);

        result.Should().NotBeNull();
        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEntitiesAsync_returns_empty_list_without_throwing()
    {
        var entities = await _client.GetEntitiesAsync("doc-123");

        entities.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task UploadDocumentAsync_throws_InvalidOperationException()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var act = async () => await _client.UploadDocumentAsync(stream, "contract.pdf", "application/pdf");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*RAG_PLATFORM_ENABLED=false*");
    }

    [Fact]
    public async Task ChatSyncAsync_throws_InvalidOperationException()
    {
        var act = async () => await _client.ChatSyncAsync(
            "extract payment terms",
            filters: null,
            responseFormat: "json");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*RAG_PLATFORM_ENABLED=false*");
    }

    [Fact]
    public void RagPlatformException_carries_status_code_and_body()
    {
        var ex = new RagPlatformException("boom", statusCode: 503, responseBody: "upstream down");

        ex.StatusCode.Should().Be(503);
        ex.ResponseBody.Should().Be("upstream down");
        ex.Message.Should().Be("boom");
    }
}
