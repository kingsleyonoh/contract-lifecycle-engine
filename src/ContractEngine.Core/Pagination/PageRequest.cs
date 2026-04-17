namespace ContractEngine.Core.Pagination;

/// <summary>
/// Input envelope for cursor-paged list endpoints. Matches the query-string contract in
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: optional <c>cursor</c>, <c>page_size</c> (default
/// 25, max 100), <c>sort_by</c> / <c>sort_dir</c>, and <c>created_after</c> / <c>created_before</c>
/// filters.
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public string? Cursor { get; init; }

    public int PageSize { get; init; } = DefaultPageSize;

    public string? SortBy { get; init; }

    /// <summary>"asc" or "desc" (default). Unknown values fall back to desc at query build time.</summary>
    public string? SortDir { get; init; } = "desc";

    public DateTime? CreatedAfter { get; init; }

    public DateTime? CreatedBefore { get; init; }

    /// <summary>
    /// Clamps an arbitrary caller-supplied page size to the inclusive range
    /// [1, <see cref="MaxPageSize"/>]. Non-positive values collapse to 1 so that SQL
    /// never receives a zero/negative LIMIT.
    /// </summary>
    public static int ClampPageSize(int requested)
    {
        if (requested < 1)
        {
            return 1;
        }

        return requested > MaxPageSize ? MaxPageSize : requested;
    }
}
