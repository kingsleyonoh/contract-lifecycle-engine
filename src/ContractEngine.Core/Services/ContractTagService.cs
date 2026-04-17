using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped orchestration for replacing the tag set on a <see cref="Contract"/>. Semantics:
/// the supplied tag list REPLACES whatever is currently on the contract (delete-existing +
/// bulk-insert atomically inside <see cref="IContractTagRepository.ReplaceTagsAsync"/>).
///
/// <para>Normalisation rules before hitting the repo:</para>
/// <list type="bullet">
///   <item>Whitespace is trimmed from each input tag.</item>
///   <item>Empty / whitespace-only tags → <see cref="ArgumentException"/> (validator rejects
///     before this point in the happy path, but defensive guard stays in the service).</item>
///   <item>Tags &gt; 100 chars → <see cref="ArgumentException"/>.</item>
///   <item>Duplicates within the request are collapsed (case-sensitive). PRD §4.12 treats
///     "Vendor" and "vendor" as distinct tags.</item>
/// </list>
///
/// <para>Passing an empty list clears every tag on the contract (idempotent reset). Missing
/// contracts raise <see cref="KeyNotFoundException"/> → 404.</para>
/// </summary>
public sealed class ContractTagService
{
    public const int MaxTagLength = 100;

    private readonly IContractTagRepository _repository;
    private readonly IContractRepository _contractRepository;
    private readonly ITenantContext _tenantContext;

    public ContractTagService(
        IContractTagRepository repository,
        IContractRepository contractRepository,
        ITenantContext tenantContext)
    {
        _repository = repository;
        _contractRepository = contractRepository;
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyList<ContractTag>> ReplaceTagsAsync(
        Guid contractId,
        IReadOnlyList<string> requestedTags,
        CancellationToken cancellationToken = default)
    {
        if (requestedTags is null)
        {
            throw new ArgumentNullException(nameof(requestedTags));
        }

        var tenantId = RequireTenantId();

        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException($"contract {contractId} not found for this tenant");
        }

        var normalized = Normalise(requestedTags);

        return await _repository.ReplaceTagsAsync(tenantId, contractId, normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractTag>> ListByContractAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId, cancellationToken);
        if (contract is null)
        {
            // Cross-tenant id guess: hide existence rather than returning 404 detail — the list
            // endpoint presents an empty collection like ContractDocumentService does for lists.
            return Array.Empty<ContractTag>();
        }

        return await _repository.ListByContractAsync(contractId, cancellationToken);
    }

    private static IReadOnlyList<string> Normalise(IReadOnlyList<string> input)
    {
        // Case-sensitive dedupe preserves ordering (first occurrence wins).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(input.Count);
        foreach (var raw in input)
        {
            if (raw is null)
            {
                throw new ArgumentException("tag value cannot be null");
            }
            var trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new ArgumentException("tag value cannot be empty or whitespace");
            }
            if (trimmed.Length > MaxTagLength)
            {
                throw new ArgumentException($"tag value exceeds {MaxTagLength}-character limit");
            }
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }
        return result;
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
