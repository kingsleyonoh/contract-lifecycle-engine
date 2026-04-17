using ContractEngine.Api.Endpoints.Dto;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Validation;
using CoreCreateObligationRequest = ContractEngine.Core.Services.CreateObligationRequest;

namespace ContractEngine.Api.Endpoints;

/// <summary>
/// Pure-static response/request mapping helpers extracted from <see cref="ObligationEndpoints"/>
/// for modularity. Every method is deterministic and side-effect-free — no I/O, no DI.
/// </summary>
internal static class ObligationResponseMapper
{
    internal static CreateObligationRequestDomain MapToDomain(CreateObligationRequestWire wire) => new()
    {
        ContractId = wire.ContractId,
        ObligationType = wire.ObligationType,
        Title = wire.Title,
        Description = wire.Description,
        ResponsibleParty = wire.ResponsibleParty,
        DeadlineDate = wire.DeadlineDate,
        DeadlineFormula = wire.DeadlineFormula,
        Recurrence = wire.Recurrence,
        Amount = wire.Amount,
        Currency = wire.Currency,
        AlertWindowDays = wire.AlertWindowDays,
        GracePeriodDays = wire.GracePeriodDays,
        BusinessDayCalendar = wire.BusinessDayCalendar,
        ClauseReference = wire.ClauseReference,
        Metadata = wire.Metadata,
    };

    internal static CoreCreateObligationRequest MapToCore(CreateObligationRequestWire wire) => new()
    {
        ContractId = wire.ContractId,
        ObligationType = wire.ObligationType,
        Title = wire.Title,
        Description = wire.Description,
        ResponsibleParty = wire.ResponsibleParty,
        DeadlineDate = wire.DeadlineDate,
        DeadlineFormula = wire.DeadlineFormula,
        Recurrence = wire.Recurrence,
        Amount = wire.Amount,
        Currency = wire.Currency,
        AlertWindowDays = wire.AlertWindowDays,
        GracePeriodDays = wire.GracePeriodDays,
        BusinessDayCalendar = wire.BusinessDayCalendar,
        ClauseReference = wire.ClauseReference,
        Metadata = wire.Metadata,
    };

    internal static ObligationResponse MapToResponse(Obligation o) => new()
    {
        Id = o.Id,
        TenantId = o.TenantId,
        ContractId = o.ContractId,
        ObligationType = o.ObligationType,
        Status = o.Status,
        Title = o.Title,
        Description = o.Description,
        ResponsibleParty = o.ResponsibleParty,
        DeadlineDate = o.DeadlineDate,
        DeadlineFormula = o.DeadlineFormula,
        Recurrence = o.Recurrence,
        NextDueDate = o.NextDueDate,
        Amount = o.Amount,
        Currency = o.Currency,
        AlertWindowDays = o.AlertWindowDays,
        GracePeriodDays = o.GracePeriodDays,
        BusinessDayCalendar = o.BusinessDayCalendar,
        Source = o.Source,
        ExtractionJobId = o.ExtractionJobId,
        ConfidenceScore = o.ConfidenceScore,
        ClauseReference = o.ClauseReference,
        Metadata = o.Metadata,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
    };

    internal static ObligationDetailResponse MapToDetail(
        Obligation o,
        IReadOnlyList<ObligationEvent> events) => new()
    {
        Id = o.Id,
        TenantId = o.TenantId,
        ContractId = o.ContractId,
        ObligationType = o.ObligationType,
        Status = o.Status,
        Title = o.Title,
        Description = o.Description,
        ResponsibleParty = o.ResponsibleParty,
        DeadlineDate = o.DeadlineDate,
        DeadlineFormula = o.DeadlineFormula,
        Recurrence = o.Recurrence,
        NextDueDate = o.NextDueDate,
        Amount = o.Amount,
        Currency = o.Currency,
        AlertWindowDays = o.AlertWindowDays,
        GracePeriodDays = o.GracePeriodDays,
        BusinessDayCalendar = o.BusinessDayCalendar,
        Source = o.Source,
        ExtractionJobId = o.ExtractionJobId,
        ConfidenceScore = o.ConfidenceScore,
        ClauseReference = o.ClauseReference,
        Metadata = o.Metadata,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
        Events = events.Select(MapEvent).ToList(),
    };

    internal static ObligationEventResponse MapEvent(ObligationEvent e) => new()
    {
        Id = e.Id,
        ObligationId = e.ObligationId,
        FromStatus = e.FromStatus,
        ToStatus = e.ToStatus,
        Actor = e.Actor,
        Reason = e.Reason,
        Metadata = e.Metadata,
        CreatedAt = e.CreatedAt,
    };

    internal static bool TryParseResolution(string raw, out DisputeResolution resolution)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "stands":
                resolution = DisputeResolution.Stands;
                return true;
            case "waived":
                resolution = DisputeResolution.Waived;
                return true;
            default:
                resolution = default;
                return false;
        }
    }

    internal static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Accept snake_case DB values (e.g. "pending", "payment") as well as PascalCase. Same
        // normalisation pattern as ContractEndpoints.ParseEnum.
        var normalized = raw.Replace("_", string.Empty);
        if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new FluentValidation.ValidationException(
            $"unknown value '{raw}' for {typeof(TEnum).Name}");
    }
}
