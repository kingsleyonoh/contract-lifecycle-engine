using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/counterparties</c> — matches the paginated envelope from
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination": { ... } }</c>.
/// </summary>
public sealed class CounterpartyListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<CounterpartyResponse> Data { get; set; } = Array.Empty<CounterpartyResponse>();

    [JsonPropertyName("pagination")]
    public CounterpartyPaginationEnvelope Pagination { get; set; } = new();

    public static CounterpartyListResponse FromPagedResult(
        PagedResult<CounterpartyResponse> paged)
    {
        return new CounterpartyListResponse
        {
            Data = paged.Data,
            Pagination = new CounterpartyPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope. Kept alongside the list response to avoid leaking a
/// hyphenated Core type through <c>System.Text.Json</c> defaults.</summary>
public sealed class CounterpartyPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
