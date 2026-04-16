using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Exceptions;

/// <summary>
/// Thrown by <see cref="Services.ObligationStateMachine"/> when a caller requests a status
/// transition that's not permitted by the PRD §4.6 state-machine map.
///
/// Derives from <see cref="EntityTransitionException"/> (and transitively
/// <see cref="InvalidOperationException"/>) so the middleware's existing 422 INVALID_TRANSITION
/// arm handles this uniformly with <see cref="ContractTransitionException"/>. Typed
/// <see cref="ObligationStatus"/> properties are retained for in-process callers (services, tests,
/// job code); the base-class string list powers the HTTP response body.
///
/// <para><see cref="ValidNextStates"/> uses <c>new</c> to shadow the base-class string list with
/// the strongly-typed enum list. This mirrors the pattern in
/// <see cref="ContractTransitionException"/>.</para>
/// </summary>
public sealed class ObligationTransitionException : EntityTransitionException
{
    public new ObligationStatus CurrentStatus { get; }

    public new ObligationStatus RequestedStatus { get; }

    /// <summary>
    /// Typed list shadowing the base-class string list. In-process consumers get
    /// <see cref="ObligationStatus"/> values directly.
    /// </summary>
    public new IReadOnlyList<ObligationStatus> ValidNextStates { get; }

    public ObligationTransitionException(
        ObligationStatus currentStatus,
        ObligationStatus requestedStatus,
        IReadOnlyList<ObligationStatus> validNextStates)
        : base(
            BuildMessage(currentStatus, requestedStatus, validNextStates),
            EnumToSnake(currentStatus.ToString()),
            EnumToSnake(requestedStatus.ToString()),
            validNextStates.Select(s => EnumToSnake(s.ToString())).ToArray())
    {
        CurrentStatus = currentStatus;
        RequestedStatus = requestedStatus;
        ValidNextStates = validNextStates;
    }

    private static string BuildMessage(
        ObligationStatus from,
        ObligationStatus to,
        IReadOnlyList<ObligationStatus> validNextStates)
    {
        var valid = validNextStates.Count == 0
            ? "none"
            : string.Join(", ", validNextStates);
        return $"invalid obligation status transition: {from} → {to}. valid next states from {from}: [{valid}]";
    }

    // Same PascalCase → snake_case helper as ContractTransitionException. Duplicated to keep Core
    // free of cross-class coupling on what is ultimately a two-line string conversion.
    private static string EnumToSnake(string value)
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
}
