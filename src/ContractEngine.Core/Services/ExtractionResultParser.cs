using System.Text.Json;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Pure-function parser that converts a RAG <c>chat_sync</c> answer into a list of
/// <see cref="Obligation"/> records for persistence. Extracted from <see cref="ExtractionService"/>
/// (Batch 026 modularity gate) so the extraction orchestrator stays focused on pipeline shape and
/// this helper owns all the JSON-to-domain-shape mapping.
///
/// <para>Invariants (load-bearing — do NOT weaken without a test):</para>
/// <list type="bullet">
///   <item>Every obligation returned carries <see cref="ObligationStatus.Pending"/> — no
///     auto-activation, extract-then-confirm per PRD §5.2.</item>
///   <item>Source is always <see cref="ObligationSource.RagExtraction"/>.</item>
///   <item>Malformed JSON returns an empty list — the calling prompt type is still counted as
///     successful if the upstream chat call succeeded; no obligations is a valid answer.</item>
/// </list>
/// </summary>
public static class ExtractionResultParser
{
    public static List<Obligation> Parse(string rawAnswer, string promptType, ExtractionJob job)
    {
        var obligations = new List<Obligation>();

        try
        {
            using var doc = JsonDocument.Parse(rawAnswer);
            if (!doc.RootElement.TryGetProperty("obligations", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return obligations;
            }

            foreach (var item in array.EnumerateArray())
            {
                obligations.Add(BuildObligation(item, promptType, job));
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — return empty; the prompt type is still counted as successful
            // if the chat call itself succeeded (the response just had no parseable obligations).
        }

        return obligations;
    }

    private static Obligation BuildObligation(JsonElement item, string promptType, ExtractionJob job)
    {
        var title = item.TryGetProperty("title", out var t)
            ? t.GetString() ?? $"Extracted {promptType} obligation"
            : $"Extracted {promptType} obligation";

        var oblType = ResolveObligationType(
            item.TryGetProperty("obligation_type", out var ot)
                ? ot.GetString()
                : promptType);

        var confidence = item.TryGetProperty("confidence", out var c)
            ? (decimal?)c.GetDouble()
            : null;

        var amount = item.TryGetProperty("amount", out var a)
            ? (decimal?)a.GetDouble()
            : null;

        var currency = item.TryGetProperty("currency", out var cur)
            ? cur.GetString() ?? "USD"
            : "USD";

        var now = DateTime.UtcNow;
        return new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = job.TenantId,
            ContractId = job.ContractId,
            ObligationType = oblType,
            Status = ObligationStatus.Pending,
            Source = ObligationSource.RagExtraction,
            ExtractionJobId = job.Id,
            Title = title,
            Description = item.TryGetProperty("description", out var d) ? d.GetString() : null,
            ConfidenceScore = confidence,
            Amount = amount,
            Currency = currency,
            ClauseReference = item.TryGetProperty("clause_reference", out var cr) ? cr.GetString() : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ObligationType ResolveObligationType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ObligationType.Other;
        }

        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<ObligationType>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return ObligationType.Other;
    }
}
