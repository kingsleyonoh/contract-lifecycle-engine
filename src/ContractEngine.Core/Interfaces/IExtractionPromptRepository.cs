using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>extraction_prompts</c> table. Unlike tenant-scoped repositories, this
/// one handles nullable tenant_id explicitly — system-default rows (<c>tenant_id IS NULL</c>) must
/// be visible to every tenant. The repository enforces the PRD §4.11 lookup chain:
/// tenant-specific → system default → null (caller falls back to <c>ExtractionDefaults</c>).
/// </summary>
public interface IExtractionPromptRepository
{
    /// <summary>
    /// Resolves a prompt for <paramref name="tenantId"/> and <paramref name="promptType"/>.
    /// Returns tenant-specific first, then system default (tenant_id = null). Returns null if
    /// neither exists — the caller should fall back to <c>ExtractionDefaults.GetByType</c>.
    /// Only returns active prompts (<c>is_active = true</c>).
    /// </summary>
    Task<ExtractionPrompt?> GetPromptAsync(
        Guid tenantId,
        string promptType,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a prompt row.</summary>
    Task AddAsync(ExtractionPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Returns all system-default prompts (tenant_id IS NULL).</summary>
    Task<IReadOnlyList<ExtractionPrompt>> ListSystemDefaultsAsync(
        CancellationToken cancellationToken = default);
}
