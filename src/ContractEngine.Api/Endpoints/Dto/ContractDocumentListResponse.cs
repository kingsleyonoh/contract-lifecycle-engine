using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/contracts/{id}/documents</c> — matches the paginated envelope from
/// <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination": { ... } }</c>.
/// </summary>
public sealed class ContractDocumentListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ContractDocumentResponse> Data { get; set; } = Array.Empty<ContractDocumentResponse>();

    [JsonPropertyName("pagination")]
    public ContractDocumentPaginationEnvelope Pagination { get; set; } = new();

    public static ContractDocumentListResponse FromPagedResult(PagedResult<ContractDocumentResponse> paged)
    {
        return new ContractDocumentListResponse
        {
            Data = paged.Data,
            Pagination = new ContractDocumentPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope, mirroring <c>ContractPaginationEnvelope</c>.</summary>
public sealed class ContractDocumentPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
