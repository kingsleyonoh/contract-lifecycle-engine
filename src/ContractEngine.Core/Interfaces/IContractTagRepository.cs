using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>contract_tags</c> table. Defined in Core so services can depend on it
/// without referencing EF Core. Tenant scoping is enforced at the query-filter level — callers
/// never pass <c>tenant_id</c> explicitly.
///
/// <para>Semantics: <see cref="ReplaceTagsAsync"/> is the sole mutation entry point. It runs the
/// delete-existing + bulk-insert atomically inside an EF Core transaction so a failure mid-way
/// leaves the UNIQUE(tenant_id, contract_id, tag) row set intact.</para>
/// </summary>
public interface IContractTagRepository
{
    /// <summary>
    /// Atomically replaces every tag row for <paramref name="contractId"/> (under the current
    /// tenant) with the supplied <paramref name="tags"/>. Existing rows not in the new set are
    /// deleted; rows already present are preserved when their tag string matches (equal sets are
    /// a no-op on the wire but still produce a single DB round-trip).
    /// </summary>
    Task<IReadOnlyList<ContractTag>> ReplaceTagsAsync(
        Guid tenantId,
        Guid contractId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all tags on the given contract for the current tenant, ordered by tag.</summary>
    Task<IReadOnlyList<ContractTag>> ListByContractAsync(
        Guid contractId,
        CancellationToken cancellationToken = default);
}
