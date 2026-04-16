namespace ContractEngine.Core.Enums;

/// <summary>
/// Business type of a contract. Mirrors the CHECK constraint on <c>contracts.contract_type</c>
/// from PRD §4.3. <see cref="Nda"/> serialises to <c>"nda"</c> (matching the PRD value) via the
/// SnakeCaseLower JSON naming policy — single-token name collapses to lowercase.
/// </summary>
public enum ContractType
{
    Vendor = 0,
    Customer = 1,
    Partnership = 2,
    Nda = 3,
    Employment = 4,
    Lease = 5,
}
