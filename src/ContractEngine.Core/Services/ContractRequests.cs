using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Services;

/// <summary>
/// Request envelope for <see cref="ContractService.CreateAsync"/>. Either
/// <see cref="CounterpartyId"/> or <see cref="CounterpartyName"/> must be supplied — never both.
/// Extracted from <c>ContractService.cs</c> (Batch 026 modularity gate) so the service file stays
/// under the 300-line cap and request shapes live beside each other.
/// </summary>
public sealed record CreateContractRequest
{
    public string Title { get; init; } = string.Empty;
    public string? ReferenceNumber { get; init; }
    public ContractType ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public string? CounterpartyName { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Partial-update request for <see cref="ContractService.UpdateAsync"/>. Every field is optional;
/// absent fields leave the stored value untouched. <see cref="ContractStatus"/> is NOT here — use
/// the lifecycle methods for transitions.
/// </summary>
public sealed record UpdateContractRequest
{
    public string? Title { get; init; }
    public string? ReferenceNumber { get; init; }
    public ContractType? ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
