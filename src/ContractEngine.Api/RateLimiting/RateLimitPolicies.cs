namespace ContractEngine.Api.RateLimiting;

/// <summary>
/// Named rate-limit policies registered in <c>Program.cs</c>. Endpoint files reference these
/// constants via <c>.RequireRateLimiting(RateLimitPolicies.X)</c>; the policy-to-permit mapping
/// follows PRD §8b per-endpoint limits. A single partition key is used per policy:
/// <list type="bullet">
///   <item>Authenticated requests (<c>X-API-Key</c> header present) partition by the hash of the
///     API key so each tenant has its own bucket.</item>
///   <item>Public requests (no header) partition by the client's remote IP address — prevents
///     a single origin from starving the public registration window.</item>
/// </list>
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Public endpoints — 5/min (tenant registration). Heavy restriction because the
    /// endpoint creates DB rows on each invocation and is the primary target for abuse.</summary>
    public const string Public = "public";

    /// <summary>Read-heavy authenticated endpoints — 100/min.</summary>
    public const string Read100 = "read-100";

    /// <summary>Standard write endpoints — 50/min.</summary>
    public const string Write50 = "write-50";

    /// <summary>Sensitive write endpoints (e.g. PATCH tenant) — 20/min.</summary>
    public const string Write20 = "write-20";

    /// <summary>High-impact write endpoints (e.g. terminate, archive) — 10/min.</summary>
    public const string Write10 = "write-10";
}
