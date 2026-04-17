using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/contracts</c> — matches the paginated envelope from
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination": { ... } }</c>.
/// </summary>
public sealed class ContractListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ContractResponse> Data { get; set; } = Array.Empty<ContractResponse>();

    [JsonPropertyName("pagination")]
    public ContractPaginationEnvelope Pagination { get; set; } = new();

    public static ContractListResponse FromPagedResult(PagedResult<ContractResponse> paged)
    {
        return new ContractListResponse
        {
            Data = paged.Data,
            Pagination = new ContractPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope, mirroring <c>CounterpartyPaginationEnvelope</c>.</summary>
public sealed class ContractPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
