using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/obligations/{id}/events</c>. Matches the paginated envelope
/// convention from <c>CODEBASE_CONTEXT.md</c> Key Patterns §2: <c>{ "data": [...], "pagination":
/// { ... } }</c>. Each row uses <see cref="ObligationEventResponse"/>.
/// </summary>
public sealed class ObligationEventListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ObligationEventResponse> Data { get; set; } = Array.Empty<ObligationEventResponse>();

    [JsonPropertyName("pagination")]
    public ObligationPaginationEnvelope Pagination { get; set; } = new();

    public static ObligationEventListResponse FromPagedResult(PagedResult<ObligationEventResponse> paged)
    {
        return new ObligationEventListResponse
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
