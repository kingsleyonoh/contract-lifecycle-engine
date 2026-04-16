using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>POST /api/contracts/{id}/tags</c>. The supplied <see cref="Tags"/> list
/// REPLACES whatever is currently on the contract — empty list clears all. Each tag must be 1-100
/// characters; duplicates in the body are deduplicated by <c>ContractTagService</c>.
/// </summary>
public sealed class PutTagsRequest
{
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

/// <summary>Flat envelope for the tag-replace response — a snake_case list of tags for the contract.</summary>
public sealed class TagListResponse
{
    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
}
