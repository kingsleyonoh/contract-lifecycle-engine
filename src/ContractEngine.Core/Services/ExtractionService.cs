using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Defaults;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Orchestrates the RAG extraction pipeline (PRD §5.2). Responsible for:
/// <list type="bullet">
///   <item><see cref="TriggerExtractionAsync"/> — creates a Queued extraction job.</item>
///   <item><see cref="ExecuteExtractionAsync"/> — called by <c>ExtractionProcessorJob</c>: uploads
///     the document to RAG (if needed), runs chat prompts, parses obligations, persists results.</item>
///   <item><see cref="RetryExtractionAsync"/> — resets a Failed/Partial job back to Queued.</item>
/// </list>
///
/// <para>Extract-then-confirm: all AI-extracted obligations are created with
/// <see cref="ObligationStatus.Pending"/> — no auto-activation. Humans confirm via the
/// <c>/api/obligations/{id}/confirm</c> endpoint.</para>
/// </summary>
public class ExtractionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IRagPlatformClient _ragClient;
    private readonly IExtractionPromptRepository _promptRepo;
    private readonly IExtractionJobRepository _jobRepo;
    private readonly IObligationRepository _obligationRepo;
    private readonly IContractDocumentRepository _docRepo;
    private readonly IDocumentStorage _storage;
    private readonly IContractRepository _contractRepo;
    private readonly ITenantContext _tenantContext;

    public ExtractionService(
        IRagPlatformClient ragClient,
        IExtractionPromptRepository promptRepo,
        IExtractionJobRepository jobRepo,
        IObligationRepository obligationRepo,
        IContractDocumentRepository docRepo,
        IDocumentStorage storage,
        IContractRepository contractRepo,
        ITenantContext tenantContext)
    {
        _ragClient = ragClient;
        _promptRepo = promptRepo;
        _jobRepo = jobRepo;
        _obligationRepo = obligationRepo;
        _docRepo = docRepo;
        _storage = storage;
        _contractRepo = contractRepo;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Creates a Queued extraction job for the given contract. Validates the contract exists
    /// (tenant-scoped). If <paramref name="documentId"/> is provided, validates it exists for
    /// this contract. The actual extraction runs asynchronously via ExtractionProcessorJob.
    /// </summary>
    public virtual async Task<ExtractionJob> TriggerExtractionAsync(
        Guid contractId,
        string[]? promptTypes,
        Guid? documentId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();

        var contract = await _contractRepo.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException(
                $"contract {contractId} not found for this tenant");
        }

        if (documentId.HasValue)
        {
            var doc = await _docRepo.GetByIdAsync(documentId.Value, cancellationToken);
            if (doc is null || doc.ContractId != contractId)
            {
                throw new KeyNotFoundException(
                    $"document {documentId} not found for contract {contractId}");
            }
        }

        var effectivePromptTypes = promptTypes is { Length: > 0 }
            ? promptTypes
            : ExtractionDefaults.AllPromptTypes.ToArray();

        var job = new ExtractionJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            DocumentId = documentId,
            Status = ExtractionStatus.Queued,
            PromptTypes = effectivePromptTypes,
            CreatedAt = DateTime.UtcNow,
        };

        await _jobRepo.AddAsync(job, cancellationToken);
        return job;
    }

    /// <summary>
    /// Executes the extraction pipeline for a queued job. Called by the ExtractionProcessorJob.
    /// Uploads the document to RAG if needed, runs each prompt type, parses obligations, and
    /// updates the job status to Completed/Partial/Failed.
    /// </summary>
    public async Task ExecuteExtractionAsync(
        ExtractionJob job,
        CancellationToken cancellationToken = default)
    {
        job.Status = ExtractionStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        await _jobRepo.UpdateAsync(job, cancellationToken);

        // Resolve the document — use the job's explicit document or find the latest for the
        // contract.
        ContractDocument? document = null;
        if (job.DocumentId.HasValue)
        {
            document = await _docRepo.GetByIdAsync(job.DocumentId.Value, cancellationToken);
        }

        // Upload to RAG if the document hasn't been uploaded yet.
        string? ragDocId = document?.RagDocumentId ?? job.RagDocumentId;
        if (document is not null && string.IsNullOrEmpty(ragDocId))
        {
            try
            {
                using var stream = await _storage.OpenReadAsync(
                    document.FilePath, cancellationToken);
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
                return;
            }
        }
        else if (!string.IsNullOrEmpty(ragDocId))
        {
            job.RagDocumentId = ragDocId;
        }

        // Run each prompt type.
        var rawResponses = job.RawResponses ?? new Dictionary<string, object>();
        var totalObligations = 0;
        var successCount = 0;
        var failedTypes = new List<string>();

        foreach (var promptType in job.PromptTypes)
        {
            // Skip already-processed prompt types (for retry scenarios).
            if (rawResponses.ContainsKey(promptType))
            {
                var existing = rawResponses[promptType];
                if (existing is not JsonElement je || !IsErrorMarker(je))
                {
                    successCount++;
                    continue;
                }
            }

            try
            {
                var promptText = await ResolvePromptTextAsync(
                    job.TenantId, promptType, cancellationToken);

                var filters = !string.IsNullOrEmpty(ragDocId)
                    ? new Dictionary<string, object> { ["document_id"] = ragDocId }
                    : null;

                var chatResult = await _ragClient.ChatSyncAsync(
                    promptText,
                    filters,
                    "json",
                    cancellationToken);

                var obligations = ParseObligations(
                    chatResult.Answer, promptType, job, cancellationToken);

                foreach (var obl in obligations)
                {
                    await _obligationRepo.AddAsync(obl, cancellationToken);
                    totalObligations++;
                }

                rawResponses[promptType] = JsonSerializer.SerializeToElement(
                    chatResult.Answer, JsonOptions);
                successCount++;
            }
            catch (Exception ex)
            {
                rawResponses[promptType] = JsonSerializer.SerializeToElement(
                    new { error = ex.Message }, JsonOptions);
                failedTypes.Add(promptType);
            }
        }

        job.RawResponses = rawResponses;
        job.ObligationsFound = totalObligations;
        job.CompletedAt = DateTime.UtcNow;

        if (failedTypes.Count == 0)
        {
            job.Status = ExtractionStatus.Completed;
        }
        else if (successCount > 0)
        {
            job.Status = ExtractionStatus.Partial;
            job.ErrorMessage = $"Failed prompt types: {string.Join(", ", failedTypes)}";
        }
        else
        {
            job.Status = ExtractionStatus.Failed;
            job.ErrorMessage = $"All prompt types failed: {string.Join(", ", failedTypes)}";
        }

        await _jobRepo.UpdateAsync(job, cancellationToken);
    }

    /// <summary>
    /// Resets a Failed or Partial job back to Queued for re-processing. Increments retry_count.
    /// Returns null if the job doesn't exist. Throws if the job status doesn't allow retry.
    /// </summary>
    public async Task<ExtractionJob?> RetryExtractionAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobRepo.GetByIdAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        if (job.Status != ExtractionStatus.Failed && job.Status != ExtractionStatus.Partial)
        {
            throw new InvalidOperationException(
                $"Cannot retry extraction job {jobId}: status is {job.Status}");
        }

        job.Status = ExtractionStatus.Queued;
        job.RetryCount++;
        job.ErrorMessage = null;
        job.CompletedAt = null;
        job.StartedAt = null;

        await _jobRepo.UpdateAsync(job, cancellationToken);
        return job;
    }

    private async Task<string> ResolvePromptTextAsync(
        Guid tenantId,
        string promptType,
        CancellationToken cancellationToken)
    {
        var prompt = await _promptRepo.GetPromptAsync(tenantId, promptType, cancellationToken);
        if (prompt is not null)
        {
            return prompt.PromptText;
        }

        return ExtractionDefaults.GetByType(promptType)
            ?? $"Extract all {promptType} obligations from this contract document.";
    }

    private List<Obligation> ParseObligations(
        string rawAnswer,
        string promptType,
        ExtractionJob job,
        CancellationToken cancellationToken)
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
                obligations.Add(new Obligation
                {
                    Id = Guid.NewGuid(),
                    TenantId = job.TenantId,
                    ContractId = job.ContractId,
                    ObligationType = oblType,
                    Status = ObligationStatus.Pending,
                    Source = ObligationSource.RagExtraction,
                    ExtractionJobId = job.Id,
                    Title = title,
                    Description = item.TryGetProperty("description", out var d)
                        ? d.GetString()
                        : null,
                    ConfidenceScore = confidence,
                    Amount = amount,
                    Currency = currency,
                    ClauseReference = item.TryGetProperty("clause_reference", out var cr)
                        ? cr.GetString()
                        : null,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — return empty; the prompt type is still counted as successful
            // if the chat call itself succeeded (the response just had no parseable obligations).
        }

        return obligations;
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

    private static bool IsErrorMarker(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("error", out _);
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }
}
