using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/obligations</c>. Matches the paginated envelope convention from
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination": { ... } }</c>.
/// </summary>
public sealed class ObligationListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ObligationResponse> Data { get; set; } = Array.Empty<ObligationResponse>();

    [JsonPropertyName("pagination")]
    public ObligationPaginationEnvelope Pagination { get; set; } = new();

    public static ObligationListResponse FromPagedResult(PagedResult<ObligationResponse> paged)
    {
        return new ObligationListResponse
        {
            Data = paged.Data,
            Pagination = new ObligationPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope. Shape matches sibling list responses.</summary>
public sealed class ObligationPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
