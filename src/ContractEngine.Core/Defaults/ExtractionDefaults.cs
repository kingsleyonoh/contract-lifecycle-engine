namespace ContractEngine.Core.Defaults;

/// <summary>
/// Hardcoded fallback extraction prompts (PRD §5.2). These are the last-resort prompts used when
/// neither a tenant-specific nor a system-default prompt exists in the database. Guarantees
/// extraction always works even with an empty <c>extraction_prompts</c> table.
/// </summary>
public static class ExtractionDefaults
{
    public const string Payment =
        "Analyze this contract and extract ALL payment obligations. For each, return JSON with: " +
        "title, description, amount, currency, deadline_formula (e.g., 'NET-30 from invoice date'), " +
        "recurrence (one_time/monthly/quarterly/annually), conditions, grace_period_days, " +
        "clause_reference (section number).";

    public const string Renewal =
        "Analyze this contract and extract ALL renewal and termination clauses. For each, return " +
        "JSON with: title, description, obligation_type (renewal/termination_notice), " +
        "notice_period_days, auto_renewal (boolean), auto_renewal_period_months, penalty_amount, " +
        "clause_reference.";

    public const string Compliance =
        "Analyze this contract and extract ALL compliance, reporting, and performance obligations. " +
        "For each, return JSON with: title, description, obligation_type " +
        "(reporting/compliance/performance), responsible_party (us/counterparty/both), " +
        "deadline_date or deadline_formula, recurrence, deliverable_description, clause_reference.";

    public const string Performance =
        "Analyze this contract and extract ALL SLA and performance commitments. For each, return " +
        "JSON with: title, description, metric, target_value, measurement_period, " +
        "penalty_for_breach, clause_reference.";

    /// <summary>All known prompt types in canonical order.</summary>
    public static readonly IReadOnlyList<string> AllPromptTypes =
        new[] { "payment", "renewal", "compliance", "performance" };

    private static readonly Dictionary<string, string> PromptsByType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["payment"] = Payment,
            ["renewal"] = Renewal,
            ["compliance"] = Compliance,
            ["performance"] = Performance,
        };

    /// <summary>
    /// Returns the hardcoded fallback prompt for the given <paramref name="promptType"/>,
    /// or <c>null</c> if the type is unknown. Case-insensitive.
    /// </summary>
    public static string? GetByType(string promptType)
    {
        if (string.IsNullOrEmpty(promptType))
            return null;

        return PromptsByType.TryGetValue(promptType, out var prompt) ? prompt : null;
    }
}
