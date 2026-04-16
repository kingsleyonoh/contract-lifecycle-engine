using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Return shape for <see cref="TenantService.RegisterAsync"/>. Carries the newly created tenant
/// row (sans secret) and the plaintext API key — which is returned to the caller <b>exactly once</b>
/// and never persisted. Callers are responsible for surfacing the plaintext key in the HTTP
/// response and immediately dropping it.
/// </summary>
public sealed record TenantRegistrationResult(Tenant Tenant, string PlaintextApiKey);
