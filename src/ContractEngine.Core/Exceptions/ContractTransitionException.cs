using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Exceptions;

/// <summary>
/// Thrown by lifecycle methods on <see cref="Services.ContractService"/> when a caller requests a
/// status transition that's not permitted by the PRD §4.3 state-machine map.
///
/// Deriving from <see cref="InvalidOperationException"/> preserves backward compatibility with the
/// generic 409 CONFLICT mapping in <c>ExceptionHandlingMiddleware</c>; however, the middleware
/// matches on the more specific type first and maps it to HTTP 422 UNPROCESSABLE_ENTITY with an
/// <c>INVALID_TRANSITION</c> error code and a details[] array listing valid next states. This
/// follows PRD §5.3, which specifies the same envelope for the future Obligation state machine.
/// </summary>
public sealed class ContractTransitionException : InvalidOperationException
{
    public ContractStatus CurrentStatus { get; }

    public ContractStatus RequestedStatus { get; }

    public IReadOnlyList<ContractStatus> ValidNextStates { get; }

    public ContractTransitionException(
        ContractStatus currentStatus,
        ContractStatus requestedStatus,
        IReadOnlyList<ContractStatus> validNextStates)
        : base(BuildMessage(currentStatus, requestedStatus, validNextStates))
    {
        CurrentStatus = currentStatus;
        RequestedStatus = requestedStatus;
        ValidNextStates = validNextStates;
    }

    private static string BuildMessage(
        ContractStatus from,
        ContractStatus to,
        IReadOnlyList<ContractStatus> validNextStates)
    {
        var valid = validNextStates.Count == 0
            ? "none"
            : string.Join(", ", validNextStates);
        return $"invalid contract status transition: {from} → {to}. valid next states from {from}: [{valid}]";
    }
}
