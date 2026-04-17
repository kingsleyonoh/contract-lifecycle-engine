using System.Text.Json;

namespace ContractEngine.Core.Integrations.Webhooks;

/// <summary>
/// Pure-function parser that normalises DocuSign <c>envelope.completed</c> and PandaDoc
/// <c>document_state_changed</c> payloads into a common <see cref="SignedContractPayload"/>.
/// Unsupported sources, non-completed events, and malformed JSON all return <c>null</c> — the
/// webhook endpoint then returns a 202 ack so the Webhook Engine stops retrying, but takes no
/// further action.
/// </summary>
public sealed class WebhookPayloadParser
{
    /// <summary>
    /// Parse a webhook payload body keyed by <paramref name="source"/> (<c>"docusign"</c> or
    /// <c>"pandadoc"</c>). Returns <c>null</c> for unsupported sources, non-actionable events,
    /// missing required fields, or malformed JSON.
    /// </summary>
    public SignedContractPayload? Parse(string? source, string? body)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonDocument? doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            return source.Trim().ToLowerInvariant() switch
            {
                "docusign" => ParseDocuSign(doc.RootElement),
                "pandadoc" => ParsePandaDoc(doc.RootElement),
                _ => null,
            };
        }
    }

    // ---------- DocuSign ----------

    private static SignedContractPayload? ParseDocuSign(JsonElement root)
    {
        if (!TryGetString(root, "event", out var eventName)
            || !string.Equals(eventName, "envelope.completed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryGetString(root, "envelope_id", out var envelopeId) || string.IsNullOrWhiteSpace(envelopeId))
        {
            return null;
        }

        if (!root.TryGetProperty("documents", out var documents)
            || documents.ValueKind != JsonValueKind.Array
            || documents.GetArrayLength() == 0)
        {
            return null;
        }

        var first = documents[0];
        if (!TryGetString(first, "download_url", out var downloadUrl)
            || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        var envelopeName = TryGetString(root, "envelope_name", out var en) && !string.IsNullOrWhiteSpace(en)
            ? en
            : envelopeId;

        // Counterparty: prefer the first signer's company; fall back to envelope_name.
        string? signerCompany = null;
        if (root.TryGetProperty("signers", out var signers)
            && signers.ValueKind == JsonValueKind.Array
            && signers.GetArrayLength() > 0)
        {
            var firstSigner = signers[0];
            if (TryGetString(firstSigner, "company", out var company) && !string.IsNullOrWhiteSpace(company))
            {
                signerCompany = company;
            }
        }

        var counterparty = signerCompany ?? envelopeName;

        var fileName = TryGetString(first, "name", out var fn) && !string.IsNullOrWhiteSpace(fn)
            ? fn
            : $"{envelopeId}.pdf";

        DateTimeOffset? completedAt = null;
        if (TryGetString(root, "completed_at", out var completedStr)
            && DateTimeOffset.TryParse(completedStr, out var parsedCompleted))
        {
            completedAt = parsedCompleted;
        }

        return new SignedContractPayload(
            Source: "docusign",
            ExternalId: envelopeId,
            Title: envelopeName,
            CounterpartyName: counterparty,
            DownloadUrl: downloadUrl,
            FileName: fileName,
            CompletedAt: completedAt);
    }

    // ---------- PandaDoc ----------

    private static SignedContractPayload? ParsePandaDoc(JsonElement root)
    {
        // Event name is either "document_state_changed" or just the old "document.state_changed".
        if (!TryGetString(root, "event", out var eventName)
            || !eventName.Contains("state_changed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetString(data, "status", out var status)
            || !string.Equals(status, "document.completed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryGetString(data, "id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!TryGetString(data, "download_url", out var downloadUrl)
            || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        var name = TryGetString(data, "name", out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : id;

        // Counterparty: pull from metadata.counterparty_name when present; fall back to name.
        string? counterpartyFromMeta = null;
        if (data.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(meta, "counterparty_name", out var cn) && !string.IsNullOrWhiteSpace(cn))
            {
                counterpartyFromMeta = cn;
            }
        }

        var counterparty = counterpartyFromMeta ?? name;

        DateTimeOffset? completedAt = null;
        if (TryGetString(data, "date_completed", out var completedStr)
            && DateTimeOffset.TryParse(completedStr, out var parsedCompleted))
        {
            completedAt = parsedCompleted;
        }

        var fileName = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}.pdf";

        return new SignedContractPayload(
            Source: "pandadoc",
            ExternalId: id,
            Title: name,
            CounterpartyName: counterparty,
            DownloadUrl: downloadUrl,
            FileName: fileName,
            CompletedAt: completedAt);
    }

    // ---------- helpers ----------

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
