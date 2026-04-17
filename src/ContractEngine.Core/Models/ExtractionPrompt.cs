namespace ContractEngine.Core.Models;

/// <summary>
/// ExtractionPrompt entity — a configurable prompt template for AI-powered obligation extraction.
/// PRD §4.11 defines the schema. Rows with <c>tenant_id = null</c> are system-wide defaults;
/// rows with a populated <c>tenant_id</c> are tenant-specific overrides.
///
/// <para><b>Why this entity does NOT implement <c>ITenantScoped</c>:</b> the global query filter
/// requires <c>TenantId == current</c>, which would hide every system-default row (tenant_id = null)
/// from every query. Prompt lookups deliberately need to see both — the repository layer queries
/// with explicit <c>WHERE tenant_id = @id OR tenant_id IS NULL</c> and prioritises tenant-specific
/// over system-default. Isolation of tenant-specific prompts is enforced at the repository layer.</para>
///
/// <para>UNIQUE constraint <c>(tenant_id, prompt_type)</c> with NULLS NOT DISTINCT — a tenant
/// cannot have two prompts for the same type, and there can only be one system-default per type.</para>
/// </summary>
public class ExtractionPrompt
{
    public Guid Id { get; set; }

    /// <summary>Null for system-default rows, set for tenant-specific overrides.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Prompt category: <c>payment</c>, <c>renewal</c>, <c>compliance</c>, <c>performance</c>.
    /// Custom types are allowed for tenant-specific prompts.
    /// </summary>
    public string PromptType { get; set; } = string.Empty;

    /// <summary>The full prompt text sent to the RAG Platform for extraction.</summary>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>
    /// Expected JSON response structure for parsing extracted obligations. JSONB column; null
    /// means the extraction service uses a generic parser.
    /// </summary>
    public Dictionary<string, object>? ResponseSchema { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
