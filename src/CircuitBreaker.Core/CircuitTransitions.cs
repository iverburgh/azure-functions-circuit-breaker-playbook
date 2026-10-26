using System.Text.Json.Serialization;
using CircuitBreaker.Contracts;
using static CircuitBreaker.Contracts.CircuitState;

namespace CircuitBreaker.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CircuitTrigger>))]
public enum CircuitTrigger
{
    FailureThresholdReached,
    RecoveryTimeoutElapsed,
    ProbesSucceeded,
    ProbeFailed,
    Isolate,
    Disable,
    Reset,
}

public sealed record CircuitTransition(CircuitState From, CircuitTrigger Trigger, CircuitState To);

/// <summary>
/// Every transition the circuit can make, in one table. Anything not listed is not a transition:
/// the engine leaves the state alone. Tests and documentation are checked against this table.
/// </summary>
public static class CircuitTransitions
{
    public static IReadOnlyList<CircuitTransition> All { get; } =
    [
        new(Closed, CircuitTrigger.FailureThresholdReached, Open),
        new(Open, CircuitTrigger.RecoveryTimeoutElapsed, HalfOpen),
        new(HalfOpen, CircuitTrigger.ProbesSucceeded, Closed),
        new(HalfOpen, CircuitTrigger.ProbeFailed, Open),

        new(Closed, CircuitTrigger.Isolate, Isolated),
        new(Open, CircuitTrigger.Isolate, Isolated),
        new(HalfOpen, CircuitTrigger.Isolate, Isolated),
        new(Disabled, CircuitTrigger.Isolate, Isolated),

        new(Closed, CircuitTrigger.Disable, Disabled),
        new(Open, CircuitTrigger.Disable, Disabled),
        new(HalfOpen, CircuitTrigger.Disable, Disabled),
        new(Isolated, CircuitTrigger.Disable, Disabled),

        new(Open, CircuitTrigger.Reset, Closed),
        new(HalfOpen, CircuitTrigger.Reset, Closed),
        new(Isolated, CircuitTrigger.Reset, Closed),
        new(Disabled, CircuitTrigger.Reset, Closed),
    ];

    private static readonly Dictionary<(CircuitState, CircuitTrigger), CircuitState> Lookup =
        All.ToDictionary(t => (t.From, t.Trigger), t => t.To);

    public static bool TryGetTarget(CircuitState from, CircuitTrigger trigger, out CircuitState to) =>
        Lookup.TryGetValue((from, trigger), out to);

    public static IReadOnlyList<CircuitAction> AllowedActions(CircuitState state)
    {
        var actions = new List<CircuitAction>();
        foreach (var (action, trigger) in ManualTriggers)
        {
            if (Lookup.ContainsKey((state, trigger)))
            {
                actions.Add(action);
            }
        }

        actions.Add(CircuitAction.Delete);
        return actions;
    }

    internal static CircuitTrigger? TriggerFor(CircuitAction action) =>
        ManualTriggers.TryGetValue(action, out var trigger) ? trigger : null;

    private static readonly Dictionary<CircuitAction, CircuitTrigger> ManualTriggers = new()
    {
        [CircuitAction.Isolate] = CircuitTrigger.Isolate,
        [CircuitAction.Disable] = CircuitTrigger.Disable,
        [CircuitAction.Reset] = CircuitTrigger.Reset,
    };

    /// <summary>Where the switch belongs in a state: only an open or isolated circuit stops work.</summary>
    public static SwitchPosition SwitchPositionFor(CircuitState state) =>
        state is Open or Isolated ? SwitchPosition.Open : SwitchPosition.Closed;
}
