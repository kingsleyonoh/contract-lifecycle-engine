using ContractEngine.Core.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Pagination;

/// <summary>
/// EF Core <see cref="IQueryable{T}"/> extension that applies the shared <c>(CreatedAt, Id)</c>
/// composite cursor described in <c>CODEBASE_CONTEXT.md</c> Key Patterns §2. Lives in Infrastructure
/// because it depends on EF Core / <c>ToListAsync</c>; the pure primitives stay in Core.
///
/// <para>Behaviour:</para>
/// <list type="bullet">
///   <item>Clamps <see cref="PageRequest.PageSize"/> to [1, 100] before issuing SQL.</item>
///   <item>When <see cref="PageRequest.Cursor"/> decodes, filters to rows strictly after the
///     cursor in the (desc CreatedAt, desc Id) ordering.</item>
///   <item>Applies optional <see cref="PageRequest.CreatedAfter"/> / <see cref="PageRequest.CreatedBefore"/>
///     bounds so callers can narrow the window.</item>
///   <item>Fetches <c>PageSize + 1</c> rows; the overshoot row is the signal that more pages
///     exist, and the last row in the returned slice produces the next cursor.</item>
///   <item>Runs <c>CountAsync</c> separately so <c>TotalCount</c> reflects the unrestricted
///     input query (before cursor filtering).</item>
/// </list>
/// </summary>
public static class CursorPaginationExtensions
{
    public static async Task<PagedResult<T>> ApplyCursorAsync<T>(
        this IQueryable<T> source,
        PageRequest request,
        CancellationToken cancellationToken = default)
        where T : class, IHasCursor
    {
        var pageSize = PageRequest.ClampPageSize(request.PageSize);
        var totalCount = await source.LongCountAsync(cancellationToken);

        var query = source;

        if (request.CreatedAfter is { } after)
        {
            query = query.Where(x => x.CreatedAt > after);
        }

        if (request.CreatedBefore is { } before)
        {
            query = query.Where(x => x.CreatedAt < before);
        }

        // Cursor filter: we only resume from a cursor if it decodes cleanly. A malformed cursor
        // falls back to page-one semantics (not an error) per Key Patterns §2.
        if (PaginationCursor.TryDecode(request.Cursor ?? string.Empty, out var decoded) && decoded is { } c)
        {
            // Strictly after in desc order ⇒ row's CreatedAt is less than the cursor's, with Id
            // tie-break so two rows sharing a timestamp still produce a total order.
            query = query.Where(x => x.CreatedAt < c.CreatedAt
                || (x.CreatedAt == c.CreatedAt && x.Id.CompareTo(c.Id) < 0));
        }

        // Fixed ordering for now — future sort_by values will branch here. Desc-by-default
        // matches the default exposed by <see cref="PageRequest"/>.
        query = query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);

        // Take one extra row to detect HasMore without needing a second query.
        var fetched = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = fetched.Count > pageSize;
        var data = hasMore ? fetched.Take(pageSize).ToList() : fetched;

        string? nextCursor = null;
        if (hasMore && data.Count > 0)
        {
            var last = data[^1];
            nextCursor = PaginationCursor.Encode(last.CreatedAt, last.Id);
        }

        return new PagedResult<T>(data, new PaginationMetadata(nextCursor, hasMore, totalCount));
    }
}
