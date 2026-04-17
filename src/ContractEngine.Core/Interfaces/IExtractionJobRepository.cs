using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>extraction_jobs</c> table. Tenant-scoped via the EF Core global
/// query filter on <see cref="ExtractionJob"/>.
/// </summary>
public interface IExtractionJobRepository
{
    Task<ExtractionJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(ExtractionJob job, CancellationToken cancellationToken = default);

    Task UpdateAsync(ExtractionJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists extraction jobs matching the supplied filters, with cursor pagination.
    /// </summary>
    Task<PagedResult<ExtractionJob>> ListAsync(
        ExtractionJobFilters filters,
        PageRequest pageRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="batchSize"/> queued jobs ordered by <c>created_at ASC</c>
    /// (oldest first). Used by the <c>ExtractionProcessorJob</c> background worker. This query
    /// uses <c>IgnoreQueryFilters</c> to scan across all tenants.
    /// </summary>
    Task<IReadOnlyList<ExtractionJob>> ListQueuedAsync(
        int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Filter criteria for <see cref="IExtractionJobRepository.ListAsync"/>.</summary>
public class ExtractionJobFilters
{
    public ExtractionStatus? Status { get; set; }
    public Guid? ContractId { get; set; }
}
