using ContractEngine.Core.Integrations.Webhooks;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Core.Tests.Integrations.Webhooks;

/// <summary>
/// Unit tests for <see cref="WebhookPayloadParser"/> (PRD §5.6c). The parser is a pure-function
/// component with no I/O — it normalises DocuSign <c>envelope.completed</c> and PandaDoc
/// <c>document_state_changed</c> payloads into a common <see cref="SignedContractPayload"/> shape
/// so the webhook endpoint can stay source-agnostic.
///
/// <para>Malformed payloads, unsupported event types (e.g. DocuSign <c>envelope.voided</c>) and
/// non-completed PandaDoc states MUST return <c>null</c> — the endpoint then returns a 202 ack so
/// the Webhook Engine does not retry, but takes no further action.</para>
/// </summary>
public class WebhookPayloadParserTests
{
    private readonly WebhookPayloadParser _parser = new();

    // ---------- DocuSign ----------

    [Fact]
    public void Parse_DocuSignEnvelopeCompleted_ReturnsNormalizedPayload()
    {
        // DocuSign envelope.completed minimal shape per PRD §5.6c + §6.1.
        var json = """
        {
          "event": "envelope.completed",
          "envelope_id": "env-12345",
          "envelope_name": "MSA with Acme Corp",
          "completed_at": "2026-04-17T10:30:00Z",
          "signers": [
            {
              "name": "Jane Doe",
              "email": "jane@acme.com",
              "company": "Acme Corp"
            }
          ],
          "documents": [
            {
              "document_id": "doc-1",
              "name": "MSA_Acme_2026.pdf",
              "download_url": "https://demo.docusign.net/restapi/v2.1/accounts/abc/envelopes/env-12345/documents/1"
            }
          ]
        }
        """;

        var result = _parser.Parse("docusign", json);

        result.Should().NotBeNull();
        result!.Source.Should().Be("docusign");
        result.ExternalId.Should().Be("env-12345");
        result.Title.Should().Be("MSA with Acme Corp");
        result.CounterpartyName.Should().Be("Acme Corp");
        result.DownloadUrl.Should().Contain("env-12345");
        result.FileName.Should().Be("MSA_Acme_2026.pdf");
        result.CompletedAt.Should().Be(new DateTimeOffset(2026, 4, 17, 10, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_DocuSignNonCompletedEvent_ReturnsNull()
    {
        // envelope.voided, envelope.sent, etc. — not actionable.
        var json = """
        {
          "event": "envelope.voided",
          "envelope_id": "env-12345"
        }
        """;

        _parser.Parse("docusign", json).Should().BeNull();
    }

    [Fact]
    public void Parse_DocuSignMissingEnvelopeId_ReturnsNull()
    {
        var json = """
        {
          "event": "envelope.completed",
          "envelope_name": "MSA"
        }
        """;

        _parser.Parse("docusign", json).Should().BeNull();
    }

    [Fact]
    public void Parse_DocuSignMissingDownloadUrl_ReturnsNull()
    {
        var json = """
        {
          "event": "envelope.completed",
          "envelope_id": "env-12345",
          "envelope_name": "MSA",
          "documents": [ { "name": "x.pdf" } ]
        }
        """;

        _parser.Parse("docusign", json).Should().BeNull();
    }

    [Fact]
    public void Parse_DocuSignNoSigners_FallsBackToEnvelopeName()
    {
        // With no signer company and no counterparty inferable, we still accept — counterparty name
        // falls back to the envelope name so downstream creates something review-able.
        var json = """
        {
          "event": "envelope.completed",
          "envelope_id": "env-67890",
          "envelope_name": "Generic Contract",
          "documents": [ { "name": "doc.pdf", "download_url": "https://demo.docusign.net/x" } ]
        }
        """;

        var result = _parser.Parse("docusign", json);

        result.Should().NotBeNull();
        result!.CounterpartyName.Should().Be("Generic Contract");
    }

    // ---------- PandaDoc ----------

    [Fact]
    public void Parse_PandaDocDocumentCompleted_ReturnsNormalizedPayload()
    {
        // PandaDoc document_state_changed with data.status = document.completed.
        var json = """
        {
          "event": "document_state_changed",
          "data": {
            "id": "doc-abc-def",
            "name": "NDA_Globex_2026",
            "status": "document.completed",
            "date_completed": "2026-04-17T11:00:00Z",
            "download_url": "https://api.pandadoc.com/public/v1/documents/doc-abc-def/download",
            "metadata": {
              "counterparty_name": "Globex Inc"
            }
          }
        }
        """;

        var result = _parser.Parse("pandadoc", json);

        result.Should().NotBeNull();
        result!.Source.Should().Be("pandadoc");
        result.ExternalId.Should().Be("doc-abc-def");
        result.Title.Should().Be("NDA_Globex_2026");
        result.CounterpartyName.Should().Be("Globex Inc");
        result.DownloadUrl.Should().Contain("doc-abc-def");
        result.FileName.Should().EndWith(".pdf");
        result.CompletedAt.Should().Be(new DateTimeOffset(2026, 4, 17, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_PandaDocNonCompletedStatus_ReturnsNull()
    {
        // status = document.draft, document.sent — not actionable.
        var json = """
        {
          "event": "document_state_changed",
          "data": {
            "id": "doc-abc",
            "status": "document.sent"
          }
        }
        """;

        _parser.Parse("pandadoc", json).Should().BeNull();
    }

    [Fact]
    public void Parse_PandaDocMissingId_ReturnsNull()
    {
        var json = """
        {
          "event": "document_state_changed",
          "data": { "status": "document.completed" }
        }
        """;

        _parser.Parse("pandadoc", json).Should().BeNull();
    }

    // ---------- generic error paths ----------

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        _parser.Parse("docusign", "not json at all").Should().BeNull();
        _parser.Parse("pandadoc", "{ \"unterminated\":").Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsNull()
    {
        _parser.Parse("docusign", "").Should().BeNull();
        _parser.Parse("pandadoc", "   ").Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownSource_ReturnsNull()
    {
        var json = """{ "event": "envelope.completed", "envelope_id": "x" }""";
        _parser.Parse("adobe-sign", json).Should().BeNull();
        _parser.Parse("", json).Should().BeNull();
    }
}
