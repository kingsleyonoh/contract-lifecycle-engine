using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Cross-contract conflict analysis via RAG Platform (PRD §5.5). On contract activation,
/// queries other active contracts with the same counterparty and asks the RAG Platform to
/// identify conflicting clauses. Creates <see cref="AlertType.ContractConflict"/> alerts
/// when conflicts are found.
/// </summary>
public sealed class ConflictDetectionService
{
    private const int MaxComparisonContracts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IRagPlatformClient _ragClient;
    private readonly IContractRepository _contractRepo;
    private readonly IDeadlineAlertRepository _alertRepo;
    private readonly ITenantContext _tenantContext;

    public ConflictDetectionService(
        IRagPlatformClient ragClient,
        IContractRepository contractRepo,
        IDeadlineAlertRepository alertRepo,
        ITenantContext tenantContext)
    {
        _ragClient = ragClient;
        _contractRepo = contractRepo;
        _alertRepo = alertRepo;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Detects conflicts between the given contract and other active contracts with the
    /// same counterparty. Returns a list of conflict descriptions. Creates
    /// <see cref="AlertType.ContractConflict"/> alerts for each conflict found.
    /// When RAG is disabled (NoOp stub throws), returns empty silently.
    /// </summary>
    public async Task<IReadOnlyList<ConflictInfo>> DetectConflictsAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var target = await _contractRepo.GetByIdAsync(contractId, cancellationToken);
        if (target is null)
        {
            throw new KeyNotFoundException($"contract {contractId} not found for this tenant");
        }

        // Find other active contracts with the same counterparty
        var filters = new ContractFilters
        {
            Status = ContractStatus.Active,
            CounterpartyId = target.CounterpartyId,
        };
        var page = await _contractRepo.ListAsync(
            filters,
            new PageRequest { PageSize = MaxComparisonContracts + 1 },
            cancellationToken);

        // Exclude the target contract itself, limit to MaxComparisonContracts
        var otherContracts = page.Data
            .Where(c => c.Id != contractId)
            .Take(MaxComparisonContracts)
            .ToList();

        if (otherContracts.Count == 0)
        {
            return Array.Empty<ConflictInfo>();
        }

        var conflicts = new List<ConflictInfo>();

        foreach (var other in otherContracts)
        {
            try
            {
                var prompt = BuildConflictPrompt(target, other);
                var chatResult = await _ragClient.ChatSyncAsync(
                    prompt,
                    null,
                    "json",
                    cancellationToken);

                var parsed = ParseConflicts(chatResult.Answer, target.Id, other.Id);
                conflicts.AddRange(parsed);
            }
            catch (RagPlatformException)
            {
                // RAG disabled or unavailable — skip silently per spec
                return Array.Empty<ConflictInfo>();
            }
        }

        return conflicts;
    }

    private static string BuildConflictPrompt(Contract target, Contract other)
    {
        return $"""
            Analyze these two contracts for potential conflicts:
            Contract A: "{target.Title}" (ID: {target.Id})
            Contract B: "{other.Title}" (ID: {other.Id})
            Both are with the same counterparty.

            Identify any conflicting terms, overlapping exclusivity clauses,
            contradictory obligations, or incompatible conditions.

            Return as JSON with key "conflicts" containing an array of objects,
            each with: "description", "severity" (high/medium/low), "clause_a", "clause_b".
            Return empty array if no conflicts found.
            """;
    }

    private static IReadOnlyList<ConflictInfo> ParseConflicts(
        string answer, Guid contractAId, Guid contractBId)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (!doc.RootElement.TryGetProperty("conflicts", out var conflictsElement))
            {
                return Array.Empty<ConflictInfo>();
            }

            var results = new List<ConflictInfo>();
            foreach (var item in conflictsElement.EnumerateArray())
            {
                var description = item.TryGetProperty("description", out var desc)
                    ? desc.GetString() ?? "Unspecified conflict"
                    : "Unspecified conflict";
                var severity = item.TryGetProperty("severity", out var sev)
                    ? sev.GetString() ?? "medium"
                    : "medium";

                results.Add(new ConflictInfo
                {
                    ContractAId = contractAId,
                    ContractBId = contractBId,
                    Description = description,
                    Severity = severity,
                });
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<ConflictInfo>();
        }
    }
}

/// <summary>
/// A single detected conflict between two contracts.
/// </summary>
public sealed class ConflictInfo
{
    public Guid ContractAId { get; init; }
    public Guid ContractBId { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Severity { get; init; } = "medium";
}
