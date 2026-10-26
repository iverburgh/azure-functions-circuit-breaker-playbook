using System.Diagnostics.Metrics;
using CircuitBreaker.Contracts;
using Microsoft.Extensions.Logging;

namespace CircuitBreaker.Function.Observability;

/// <summary>Counters for dashboards and alerts. Export them with OpenTelemetry; see docs/observability-and-testing.md.</summary>
public sealed class CircuitMetrics
{
    public const string MeterName = "CircuitBreaker";

    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _transitions;
    private readonly Counter<long> _probeRequests;
    private readonly Counter<long> _switchUpdates;

    public CircuitMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _outcomes = meter.CreateCounter<long>("circuitbreaker.outcomes", description: "Reported call outcomes, by result and failure category.");
        _transitions = meter.CreateCounter<long>("circuitbreaker.transitions", description: "State transitions, by source state, target state and trigger.");
        _probeRequests = meter.CreateCounter<long>("circuitbreaker.probe_requests", description: "Requests for a probe permit, by result.");
        _switchUpdates = meter.CreateCounter<long>("circuitbreaker.switch_updates", description: "Attempts to flip a switch, by result.");
    }

    public void RecordOutcomes(string circuit, OutcomeReport report)
    {
        if (report.Successes > 0)
        {
            _outcomes.Add(report.Successes, Tag("circuit", circuit), Tag("result", "success"), Tag("category", "none"));
        }

        foreach (var failure in report.Failures.Where(f => f.Count > 0))
        {
            _outcomes.Add(failure.Count, Tag("circuit", circuit), Tag("result", "failure"), Tag("category", failure.Category.ToString()));
        }

        foreach (var probe in report.ProbeResults)
        {
            _outcomes.Add(1, Tag("circuit", circuit), Tag("result", probe.Succeeded ? "probe-success" : "probe-failure"),
                Tag("category", probe.Category?.ToString() ?? "none"));
        }
    }

    public void RecordTransition(string circuit, TransitionRecord transition) =>
        _transitions.Add(1, Tag("circuit", circuit), Tag("from", transition.From.ToString()), Tag("to", transition.To.ToString()), Tag("trigger", transition.Trigger));

    public void RecordProbeRequest(string circuit, ProbePermit permit) =>
        _probeRequests.Add(1, Tag("circuit", circuit), Tag("state", permit.State.ToString()),
            Tag("result", permit.Granted ? (permit.ProbeId is null ? "not-needed" : "granted") : "denied"));

    public void RecordSwitchUpdate(string circuit, bool succeeded) =>
        _switchUpdates.Add(1, Tag("circuit", circuit), Tag("result", succeeded ? "success" : "failure"));

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}

/// <summary>Structured log events. Each has a stable event id, so alerts and queries do not depend on message text.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Circuit {CircuitKey} registered as '{DisplayName}' in state {State}")]
    public static partial void CircuitRegistered(this ILogger logger, string circuitKey, string displayName, CircuitState state);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Circuit {CircuitKey} went from {From} to {To} via {Trigger} (generation {Generation}): {Reason} Actor: {Actor}")]
    public static partial void CircuitStopped(this ILogger logger, string circuitKey, CircuitState from, CircuitState to, string trigger, long generation, string? reason, string? actor);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Circuit {CircuitKey} went from {From} to {To} via {Trigger} (generation {Generation}): {Reason} Actor: {Actor}")]
    public static partial void CircuitTransitioned(this ILogger logger, string circuitKey, CircuitState from, CircuitState to, string trigger, long generation, string? reason, string? actor);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Probe permit for circuit {CircuitKey}: granted {Granted}, state {State}, probe {ProbeId}")]
    public static partial void ProbeRequested(this ILogger logger, string circuitKey, bool granted, CircuitState state, Guid? probeId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Switch {SwitchTarget} of circuit {CircuitKey} set to {Position}")]
    public static partial void SwitchApplied(this ILogger logger, string circuitKey, string switchTarget, SwitchPosition position);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Error, Message = "Switch {SwitchTarget} of circuit {CircuitKey} could not be set to {Position} (attempt {Attempt}); retrying at {RetryAt}")]
    public static partial void SwitchFailed(this ILogger logger, Exception exception, string circuitKey, string switchTarget, SwitchPosition position, int attempt, DateTimeOffset retryAt);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Circuit {CircuitKey}: {Operation} changed nothing: {Detail}")]
    public static partial void OperationIgnored(this ILogger logger, string circuitKey, string operation, string detail);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information, Message = "Circuit {CircuitKey} deleted: {Reason}")]
    public static partial void CircuitDeleted(this ILogger logger, string circuitKey, string reason);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "Operation {Operation} for unknown circuit {CircuitKey} ignored; the caller should register it first")]
    public static partial void UnknownCircuit(this ILogger logger, string circuitKey, string operation);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Manual action {Action} on circuit {CircuitKey} by {Actor}: accepted {Accepted}. {Message} Reason: {Reason}")]
    public static partial void ManualAction(this ILogger logger, string circuitKey, CircuitAction action, string actor, bool accepted, string message, string reason);
}
