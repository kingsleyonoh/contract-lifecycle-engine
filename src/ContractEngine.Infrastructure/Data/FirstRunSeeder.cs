using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// First-run onboarding helper (PRD §11). Creates the default tenant with a freshly-minted API
/// key, surfaces the plaintext key to the caller exactly once, and ensures holiday calendars are
/// seeded.
///
/// <para>Idempotency contract (PRD §11): running when <b>any</b> tenant already exists is a no-op
/// — <see cref="RunAsync"/> returns <c>null</c>, the CLI driver prints
/// <c>"Already initialized — {N} tenant(s) exist."</c>. Running again via the same command is
/// safe.</para>
///
/// <para>Two entry points:</para>
/// <list type="bullet">
///   <item><see cref="RunAsync"/> — the production path. Reads <c>DEFAULT_TENANT_NAME</c> from the
///     caller's configuration (defaults to <c>"Default"</c>). Skips when any tenant exists.</item>
///   <item><see cref="RunForTenantAsync"/> — test / multi-environment path that skips the global
///     "any tenants exist?" guard and instead checks for a tenant with the supplied name. Lets
///     per-env bootstraps coexist in one shared DB.</item>
/// </list>
/// </summary>
public sealed class FirstRunSeeder
{
    private readonly ContractDbContext _db;
    private readonly TenantService _tenantService;
    private readonly ILogger<FirstRunSeeder> _logger;

    public FirstRunSeeder(
        ContractDbContext db,
        TenantService tenantService,
        ILogger<FirstRunSeeder> logger)
    {
        _db = db;
        _tenantService = tenantService;
        _logger = logger;
    }

    /// <summary>
    /// Production entry point. If any tenant exists in the DB → returns <c>null</c> (the caller
    /// prints "Already initialized"). Otherwise creates a tenant named <paramref name="defaultName"/>
    /// (defaults to <c>"Default"</c>), seeds holiday calendars idempotently, and returns the plaintext
    /// API key to the caller.
    /// </summary>
    public async Task<FirstRunSeedResult?> RunAsync(
        string defaultName = "Default",
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters because the scanner context is null-tenant — without it the tenant
        // global query filter returns an empty result and we'd always think the DB is empty.
        var anyTenants = await _db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(cancellationToken);
        if (anyTenants)
        {
            var count = await _db.Tenants.IgnoreQueryFilters().CountAsync(cancellationToken);
            _logger.LogInformation(
                "FirstRunSeeder: skipping — {Count} tenant(s) already exist", count);
            return null;
        }

        return await CreateTenantAndSeedAsync(defaultName, cancellationToken);
    }

    /// <summary>
    /// Test / multi-env entry point. Creates a tenant named <paramref name="tenantName"/> only if
    /// no tenant with that exact name already exists. Skips the global "any tenants exist" guard
    /// so the shared <c>contract_engine_test</c> DB (which typically has tenants from other tests)
    /// can host seeder-specific tests without polluting other fixtures.
    /// </summary>
    public async Task<FirstRunSeedResult?> RunForTenantAsync(
        string tenantName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            throw new ArgumentException("tenantName is required", nameof(tenantName));
        }

        var existing = await _db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Name == tenantName, cancellationToken);
        if (existing)
        {
            _logger.LogInformation(
                "FirstRunSeeder: tenant '{Name}' already exists — skipping", tenantName);
            return null;
        }

        return await CreateTenantAndSeedAsync(tenantName, cancellationToken);
    }

    private async Task<FirstRunSeedResult> CreateTenantAndSeedAsync(
        string name, CancellationToken cancellationToken)
    {
        var registration = await _tenantService.RegisterAsync(
            name, defaultTimezone: "UTC", defaultCurrency: "USD", cancellationToken);

        // Holiday calendars are already auto-seeded on boot via the HolidayCalendarSeeder, but the
        // PRD §11 contract says the seed script ensures calendars exist — call it here so a caller
        // that only runs --seed (and has AUTO_SEED=false) still gets a working DB.
        await HolidayCalendarSeeder.SeedAsync(_db, cancellationToken);

        _logger.LogInformation(
            "FirstRunSeeder: created tenant '{Name}' (id={Id}) and seeded holiday calendars",
            registration.Tenant.Name, registration.Tenant.Id);

        return new FirstRunSeedResult(registration.Tenant, registration.PlaintextApiKey);
    }
}

/// <summary>
/// Return envelope for <see cref="FirstRunSeeder.RunAsync"/> / <see cref="FirstRunSeeder.RunForTenantAsync"/>.
/// Plaintext API key is surfaced exactly once — the caller (CLI driver) prints it and discards it.
/// </summary>
public sealed record FirstRunSeedResult(Tenant Tenant, string PlaintextApiKey);
