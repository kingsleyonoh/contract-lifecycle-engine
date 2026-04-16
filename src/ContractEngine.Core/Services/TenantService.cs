using System.Security.Cryptography;
using System.Text;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Creates new tenants, including the cryptographic bits: a <c>cle_live_{32_hex}</c> API key,
/// its SHA-256 hash (what we persist), and a short human-readable prefix for display. Plaintext
/// keys are surfaced to the caller via <see cref="TenantRegistrationResult"/> exactly once.
///
/// Key format aligns with PRD §8b Authentication and <c>CODEBASE_CONTEXT.md</c> Key Patterns §3.
/// </summary>
public sealed class TenantService
{
    /// <summary>Fixed prefix that identifies production keys (vs future <c>cle_test_</c>).</summary>
    public const string KeyPrefix = "cle_live_";

    /// <summary>Bytes of entropy per key (32 hex chars = 16 bytes).</summary>
    private const int KeyEntropyBytes = 16;

    /// <summary>Length of the display prefix stored on the tenant row.</summary>
    private const int DisplayPrefixLength = 12;

    private readonly ITenantRepository _repository;

    public TenantService(ITenantRepository repository)
    {
        _repository = repository;
    }

    public async Task<TenantRegistrationResult> RegisterAsync(
        string name,
        string? defaultTimezone,
        string? defaultCurrency,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name must be provided", nameof(name));
        }

        var plaintextKey = GenerateApiKey();
        var keyHash = Sha256Hex(plaintextKey);
        var displayPrefix = plaintextKey[..DisplayPrefixLength];

        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            ApiKeyHash = keyHash,
            ApiKeyPrefix = displayPrefix,
            DefaultTimezone = string.IsNullOrWhiteSpace(defaultTimezone) ? "UTC" : defaultTimezone,
            DefaultCurrency = string.IsNullOrWhiteSpace(defaultCurrency) ? "USD" : defaultCurrency,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.AddAsync(tenant, cancellationToken);
        return new TenantRegistrationResult(tenant, plaintextKey);
    }

    /// <summary>
    /// Public helper so <c>TenantResolutionMiddleware</c> can hash an incoming header value
    /// using the same algorithm we used to store it.
    /// </summary>
    public static string HashApiKey(string plaintextKey) => Sha256Hex(plaintextKey);

    private static string GenerateApiKey()
    {
        Span<byte> buffer = stackalloc byte[KeyEntropyBytes];
        RandomNumberGenerator.Fill(buffer);
        var hex = Convert.ToHexString(buffer).ToLowerInvariant();
        return KeyPrefix + hex;
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
