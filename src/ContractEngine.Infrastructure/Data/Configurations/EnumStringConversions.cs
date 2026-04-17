namespace ContractEngine.Infrastructure.Data.Configurations;

/// <summary>
/// Shared helpers for EF Core enum ↔ snake_case string value conversions. Extracted from the
/// former <c>ContractDbContext</c> god-class so every <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{T}"/>
/// below can reuse the same serialisation without a static import dance.
/// </summary>
internal static class EnumStringConversions
{
    /// <summary>
    /// Mirrors <c>JsonNamingPolicy.SnakeCaseLower</c>: PascalCase → snake_case (lowercase).
    /// </summary>
    internal static string EnumToSnake(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Tolerates either "active" (DB) or "Active" (legacy) thanks to ignoreCase = true. Strips
    /// underscores before <c>Enum.Parse</c> so "termination_notice" → "TerminationNotice".
    /// </summary>
    internal static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        var normalized = value.Replace("_", string.Empty);
        return Enum.Parse<TEnum>(normalized, ignoreCase: true);
    }
}
