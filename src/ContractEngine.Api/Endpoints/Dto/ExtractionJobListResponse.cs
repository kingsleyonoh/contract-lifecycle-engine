using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire envelope for <c>GET /api/extraction-jobs</c>.</summary>
public sealed class ExtractionJobListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ExtractionJobResponse> Data { get; set; } = Array.Empty<ExtractionJobResponse>();

    [JsonPropertyName("pagination")]
    public PaginationMetadata Pagination { get; set; } = new(null, false, 0);

    public static ExtractionJobListResponse FromPagedResult(
        PagedResult<ExtractionJobResponse> paged) => new()
    {
        Data = paged.Data,
        Pagination = paged.Pagination,
    };
}
