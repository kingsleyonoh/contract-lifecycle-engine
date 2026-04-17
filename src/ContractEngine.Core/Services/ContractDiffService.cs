using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Semantic version comparison via RAG Platform (PRD §5.5). Loads two contract versions,
/// validates both have associated documents uploaded to RAG, calls ChatSyncAsync with a diff
/// prompt, parses the result into JSONB, and stores it on the newer version's diff_result.
/// </summary>
public sealed class ContractDiffService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IRagPlatformClient _ragClient;
    private readonly IContractVersionRepository _versionRepo;
    private readonly IContractDocumentRepository _docRepo;
    private readonly ITenantContext _tenantContext;

    public ContractDiffService(
        IRagPlatformClient ragClient,
        IContractVersionRepository versionRepo,
        IContractDocumentRepository docRepo,
        ITenantContext tenantContext)
    {
        _ragClient = ragClient;
        _versionRepo = versionRepo;
        _docRepo = docRepo;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Compares two contract versions using the RAG Platform. Both versions must have associated
    /// documents with rag_document_id set. Returns a result indicating success/failure and the
    /// diff content.
    /// </summary>
    public async Task<VersionDiffResult> DiffVersionsAsync(
        Guid contractId,
        int versionA,
        int versionB,
        CancellationToken cancellationToken = default)
    {
        var verA = await _versionRepo.GetByVersionNumberAsync(contractId, versionA, cancellationToken);
        if (verA is null)
        {
            throw new KeyNotFoundException(
                $"version {versionA} not found for contract {contractId}");
        }

        var verB = await _versionRepo.GetByVersionNumberAsync(contractId, versionB, cancellationToken);
        if (verB is null)
        {
            throw new KeyNotFoundException(
                $"version {versionB} not found for contract {contractId}");
        }

        // Load all docs for this contract and find those with rag_document_id
        var docsPage = await _docRepo.ListByContractAsync(
            contractId,
            new PageRequest { PageSize = PageRequest.MaxPageSize },
            cancellationToken);

        var ragDocs = docsPage.Data.Where(d => !string.IsNullOrEmpty(d.RagDocumentId)).ToList();

        if (ragDocs.Count < 2)
        {
            throw new InvalidOperationException(
                "Upload documents and wait for RAG ingestion first — both versions need " +
                "associated documents with rag_document_id to produce a diff.");
        }

        // Build the diff prompt
        var ragDocIds = ragDocs.Select(d => d.RagDocumentId!).ToList();
        var prompt = BuildDiffPrompt(versionA, versionB);

        try
        {
            var filters = new Dictionary<string, object>
            {
                ["document_ids"] = ragDocIds,
            };

            var chatResult = await _ragClient.ChatSyncAsync(
                prompt,
                filters,
                "json",
                cancellationToken);

            // Parse the answer into a dictionary for JSONB storage
            Dictionary<string, object>? diffDict = null;
            try
            {
                diffDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    chatResult.Answer, JsonOptions);
            }
            catch (JsonException)
            {
                // If parsing fails, store the raw answer
                diffDict = new Dictionary<string, object>
                {
                    ["raw_answer"] = chatResult.Answer,
                };
            }

            // Store on the newer version
            var newerVersion = verA.VersionNumber > verB.VersionNumber ? verA : verB;
            newerVersion.DiffResult = diffDict;
            await _versionRepo.UpdateAsync(newerVersion, cancellationToken);

            return new VersionDiffResult
            {
                Success = true,
                DiffData = diffDict,
                VersionA = versionA,
                VersionB = versionB,
            };
        }
        catch (RagPlatformException ex)
        {
            return new VersionDiffResult
            {
                Success = false,
                ErrorMessage = $"RAG Platform error: {ex.Message}",
                VersionA = versionA,
                VersionB = versionB,
            };
        }
    }

    private static string BuildDiffPrompt(int versionA, int versionB)
    {
        return $"""
            Compare contract version {versionA} and version {versionB}.
            Identify:
            (a) clauses added,
            (b) clauses removed,
            (c) clauses modified — for each show old text, new text, plain-English summary.
            Focus on legally significant changes.
            Return as JSON with keys: clauses_added, clauses_removed, clauses_modified.
            Each modified clause should have: clause_reference, old_text, new_text, summary.
            """;
    }
}

/// <summary>
/// Result envelope for <see cref="ContractDiffService.DiffVersionsAsync"/>. When
/// <see cref="Success"/> is false, <see cref="ErrorMessage"/> explains why (e.g. RAG disabled).
/// </summary>
public sealed class VersionDiffResult
{
    public bool Success { get; init; }
    public Dictionary<string, object>? DiffData { get; init; }
    public string? ErrorMessage { get; init; }
    public int VersionA { get; init; }
    public int VersionB { get; init; }
}
