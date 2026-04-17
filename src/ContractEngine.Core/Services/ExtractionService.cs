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
/// <c>/api/obligations/{id}/confirm</c> endpoint. Obligation shape mapping lives in
/// <see cref="ExtractionResultParser"/>; per-job pipeline helpers live in
/// <c>ExtractionService.Pipeline.cs</c> for modularity.</para>
/// </summary>
public partial class ExtractionService
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

        var (uploadOk, ragDocId) = await UploadDocumentIfNeededAsync(job, cancellationToken);
        if (!uploadOk)
        {
            return; // job already marked Failed + persisted by the helper.
        }

        var rawResponses = job.RawResponses ?? new Dictionary<string, object>();
        var totalObligations = 0;
        var successCount = 0;
        var failedTypes = new List<string>();

        foreach (var promptType in job.PromptTypes)
        {
            if (AlreadyProcessedSuccessfully(rawResponses, promptType))
            {
                successCount++;
                continue;
            }

            var outcome = await RunPromptTypeAsync(
                job, promptType, ragDocId, rawResponses, cancellationToken);
            totalObligations += outcome.ObligationsAdded;
            if (outcome.Succeeded)
            {
                successCount++;
            }
            else
            {
                failedTypes.Add(promptType);
            }
        }

        job.RawResponses = rawResponses;
        job.ObligationsFound = totalObligations;
        job.CompletedAt = DateTime.UtcNow;
        ClassifyJobOutcome(job, failedTypes, successCount);

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

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }

    private readonly record struct PromptOutcome(bool Succeeded, int ObligationsAdded);
}
