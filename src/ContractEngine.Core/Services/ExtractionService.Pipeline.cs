using System.Text.Json;
using ContractEngine.Core.Defaults;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Pipeline-stage helpers for <see cref="ExtractionService.ExecuteExtractionAsync"/>. Split into a
/// partial file so the main class stays focused on the public surface (trigger / execute / retry)
/// and the per-job mechanics (upload, per-prompt call, outcome classification) live here.
/// </summary>
public partial class ExtractionService
{
    // Resolves / uploads the RAG document handle for this job. Returns a flag indicating whether
    // the caller may proceed (false → job already marked Failed + persisted by this helper).
    private async Task<(bool Proceed, string? RagDocId)> UploadDocumentIfNeededAsync(
        ExtractionJob job,
        CancellationToken cancellationToken)
    {
        ContractDocument? document = null;
        if (job.DocumentId.HasValue)
        {
            document = await _docRepo.GetByIdAsync(job.DocumentId.Value, cancellationToken);
        }

        string? ragDocId = document?.RagDocumentId ?? job.RagDocumentId;
        if (document is not null && string.IsNullOrEmpty(ragDocId))
        {
            try
            {
                using var stream = await _storage.OpenReadAsync(document.FilePath, cancellationToken);
                var ragDoc = await _ragClient.UploadDocumentAsync(
                    stream,
                    document.FileName,
                    document.MimeType ?? "application/octet-stream",
                    cancellationToken);

                ragDocId = ragDoc.Id;
                document.RagDocumentId = ragDocId;
                await _docRepo.UpdateAsync(document, cancellationToken);
                job.RagDocumentId = ragDocId;
            }
            catch (Exception ex)
            {
                job.Status = ExtractionStatus.Failed;
                job.ErrorMessage = $"RAG Platform upload failed: {ex.Message}";
                job.CompletedAt = DateTime.UtcNow;
                await _jobRepo.UpdateAsync(job, cancellationToken);
                return (false, null);
            }
        }
        else if (!string.IsNullOrEmpty(ragDocId))
        {
            job.RagDocumentId = ragDocId;
        }

        return (true, ragDocId);
    }

    // Runs one prompt type end-to-end: resolve prompt text, call chat_sync, parse obligations,
    // persist each, stash raw response. Error-path writes an error marker into rawResponses so
    // a subsequent retry will re-run just that prompt type.
    private async Task<PromptOutcome> RunPromptTypeAsync(
        ExtractionJob job,
        string promptType,
        string? ragDocId,
        Dictionary<string, object> rawResponses,
        CancellationToken cancellationToken)
    {
        try
        {
            var promptText = await ResolvePromptTextAsync(job.TenantId, promptType, cancellationToken);

            var filters = !string.IsNullOrEmpty(ragDocId)
                ? new Dictionary<string, object> { ["document_id"] = ragDocId }
                : null;

            var chatResult = await _ragClient.ChatSyncAsync(promptText, filters, "json", cancellationToken);

            var obligations = ExtractionResultParser.Parse(chatResult.Answer, promptType, job);
            foreach (var obl in obligations)
            {
                await _obligationRepo.AddAsync(obl, cancellationToken);
            }

            rawResponses[promptType] = JsonSerializer.SerializeToElement(chatResult.Answer, JsonOptions);
            return new PromptOutcome(Succeeded: true, ObligationsAdded: obligations.Count);
        }
        catch (Exception ex)
        {
            rawResponses[promptType] = JsonSerializer.SerializeToElement(
                new { error = ex.Message }, JsonOptions);
            return new PromptOutcome(Succeeded: false, ObligationsAdded: 0);
        }
    }

    // Classifies the terminal job status from the per-prompt tallies. Pulled out so the main
    // pipeline reads as one short ladder: upload → loop → classify → persist.
    private static void ClassifyJobOutcome(
        ExtractionJob job, List<string> failedTypes, int successCount)
    {
        if (failedTypes.Count == 0)
        {
            job.Status = ExtractionStatus.Completed;
            return;
        }

        if (successCount > 0)
        {
            job.Status = ExtractionStatus.Partial;
            job.ErrorMessage = $"Failed prompt types: {string.Join(", ", failedTypes)}";
            return;
        }

        job.Status = ExtractionStatus.Failed;
        job.ErrorMessage = $"All prompt types failed: {string.Join(", ", failedTypes)}";
    }

    private static bool AlreadyProcessedSuccessfully(
        Dictionary<string, object> rawResponses, string promptType)
    {
        if (!rawResponses.TryGetValue(promptType, out var existing))
        {
            return false;
        }

        // A value present that is NOT an error marker means a previous run already succeeded for
        // this prompt type — skip on retry.
        return existing is not JsonElement je || !IsErrorMarker(je);
    }

    private async Task<string> ResolvePromptTextAsync(
        Guid tenantId, string promptType, CancellationToken cancellationToken)
    {
        var prompt = await _promptRepo.GetPromptAsync(tenantId, promptType, cancellationToken);
        if (prompt is not null)
        {
            return prompt.PromptText;
        }

        return ExtractionDefaults.GetByType(promptType)
            ?? $"Extract all {promptType} obligations from this contract document.";
    }

    private static bool IsErrorMarker(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("error", out _);
    }
}
