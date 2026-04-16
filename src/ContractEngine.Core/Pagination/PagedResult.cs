namespace ContractEngine.Core.Pagination;

/// <summary>
/// Response envelope for cursor-paged endpoints. Mirrors the wire shape documented in
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2:
/// <c>{ "data": [...], "pagination": { "next_cursor", "has_more", "total_count" } }</c>.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Data, PaginationMetadata Pagination);

public sealed record PaginationMetadata(string? NextCursor, bool HasMore, long TotalCount);
