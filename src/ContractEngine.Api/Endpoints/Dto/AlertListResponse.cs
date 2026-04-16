using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/alerts</c>. Matches the paginated envelope convention from
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination": { ... } }</c>.
/// </summary>
public sealed class AlertListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<AlertResponse> Data { get; set; } = Array.Empty<AlertResponse>();

    [JsonPropertyName("pagination")]
    public AlertPaginationEnvelope Pagination { get; set; } = new();

    public static AlertListResponse FromPagedResult(PagedResult<AlertResponse> paged)
    {
        return new AlertListResponse
        {
            Data = paged.Data,
            Pagination = new AlertPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope. Shape matches sibling list responses.</summary>
public sealed class AlertPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
