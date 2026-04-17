using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Exceptions;

/// <summary>
/// Thrown by lifecycle methods on <see cref="Services.ContractService"/> when a caller requests a
/// status transition that's not permitted by the PRD §4.3 state-machine map.
///
/// Derives from <see cref="EntityTransitionException"/> (and transitively from
/// <see cref="InvalidOperationException"/>) so the middleware's 422 INVALID_TRANSITION arm handles
/// both contract and obligation transition errors uniformly. Typed
/// <see cref="ContractStatus"/> properties are retained for in-process callers (tests, service
/// code); the base-class <c>ValidNextStates</c> (string list) powers the HTTP response body.
///
/// <para>The <see cref="ValidNextStates"/> property below uses <c>new</c> to shadow the base-class
/// string list with the strongly-typed enum list. Existing callers written before the
/// <c>EntityTransitionException</c> refactor (Batch 011) still see the typed list they expected.</para>
/// </summary>
public sealed class ContractTransitionException : EntityTransitionException
{
    public new ContractStatus CurrentStatus { get; }

    public new ContractStatus RequestedStatus { get; }

    /// <summary>
    /// Typed list shadowing the base-class string list. In-process consumers (services, tests) get
    /// <see cref="ContractStatus"/> values; callers who have the base type in hand (middleware)
    /// see the snake_case string list.
    /// </summary>
    public new IReadOnlyList<ContractStatus> ValidNextStates { get; }

    public ContractTransitionException(
        ContractStatus currentStatus,
        ContractStatus requestedStatus,
        IReadOnlyList<ContractStatus> validNextStates)
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
        ContractStatus from,
        ContractStatus to,
        IReadOnlyList<ContractStatus> validNextStates)
    {
        var valid = validNextStates.Count == 0
            ? "none"
            : string.Join(", ", validNextStates);
        return $"invalid contract status transition: {from} → {to}. valid next states from {from}: [{valid}]";
    }

    // Mirrors JsonNamingPolicy.SnakeCaseLower — identical helper lives in ContractDbContext; kept
    // local here to avoid a Core → Infrastructure dependency. PascalCase → snake_case (lowercase).
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
