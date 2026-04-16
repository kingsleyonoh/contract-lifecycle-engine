using System.Text.Json.Serialization;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>POST /api/contracts/{id}/versions</c>. Every field is optional; the server
/// auto-increments <c>version_number</c> from <c>contracts.current_version</c>.
/// </summary>
public sealed class CreateVersionRequest
{
    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; set; }

    [JsonPropertyName("effective_date")]
    public DateOnly? EffectiveDate { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }
}

/// <summary>Wire shape returned by the version endpoints.</summary>
public sealed class ContractVersionResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("version_number")]
    public int VersionNumber { get; set; }

    [JsonPropertyName("change_summary")]
    public string? ChangeSummary { get; set; }

    [JsonPropertyName("diff_result")]
    public Dictionary<string, object>? DiffResult { get; set; }

    [JsonPropertyName("effective_date")]
    public DateOnly? EffectiveDate { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>Paginated envelope for <c>GET /api/contracts/{id}/versions</c>.</summary>
public sealed class ContractVersionListResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ContractVersionResponse> Data { get; set; } = Array.Empty<ContractVersionResponse>();

    [JsonPropertyName("pagination")]
    public ContractVersionPaginationEnvelope Pagination { get; set; } = new();

    public static ContractVersionListResponse FromPagedResult(PagedResult<ContractVersionResponse> paged)
    {
        return new ContractVersionListResponse
        {
            Data = paged.Data,
            Pagination = new ContractVersionPaginationEnvelope
            {
                NextCursor = paged.Pagination.NextCursor,
                HasMore = paged.Pagination.HasMore,
                TotalCount = paged.Pagination.TotalCount,
            },
        };
    }
}

/// <summary>Snake_case pagination envelope — mirrors the other list endpoints.</summary>
public sealed class ContractVersionPaginationEnvelope
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("total_count")]
    public long TotalCount { get; set; }
}
