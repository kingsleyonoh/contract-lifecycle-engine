namespace ContractEngine.Core.Exceptions;

/// <summary>
/// Base class for state-machine transition errors across domain entities (Contract, Obligation,
/// etc.). Centralises the HTTP mapping in <c>ExceptionHandlingMiddleware</c> — any derived type is
/// serialised as 422 UNPROCESSABLE_ENTITY with the <c>INVALID_TRANSITION</c> code and a
/// <c>details[]</c> array listing valid next states, matching PRD §5.3.
///
/// <para><see cref="ValidNextStates"/> is exposed as <c>IReadOnlyList&lt;string&gt;</c> to keep the
/// middleware free of enum-type coupling; derived classes convert their typed enum list to strings
/// when constructing the exception.</para>
///
/// <para>Derives from <see cref="InvalidOperationException"/> so legacy <c>catch</c> blocks that
/// treated invalid transitions as 409 CONFLICT keep compiling; the middleware matches the more
/// specific <see cref="EntityTransitionException"/> arm before the generic 409 mapping.</para>
/// </summary>
public abstract class EntityTransitionException : InvalidOperationException
{
    /// <summary>Current status of the entity as a lowercase snake_case string (matches wire format).</summary>
    public string CurrentStatus { get; }

    /// <summary>Requested next status as a lowercase snake_case string (matches wire format).</summary>
    public string RequestedStatus { get; }

    /// <summary>
    /// Valid next statuses from <see cref="CurrentStatus"/>, in snake_case lowercase form.
    /// Populated by the middleware into the <c>details[]</c> array under the
    /// <c>valid_next_states</c> field name.
    /// </summary>
    public IReadOnlyList<string> ValidNextStates { get; }

    protected EntityTransitionException(
        string message,
        string currentStatus,
        string requestedStatus,
        IReadOnlyList<string> validNextStates)
        : base(message)
    {
        CurrentStatus = currentStatus;
        RequestedStatus = requestedStatus;
        ValidNextStates = validNextStates;
    }
}
