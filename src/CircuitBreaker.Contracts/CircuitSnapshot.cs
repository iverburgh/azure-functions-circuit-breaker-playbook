namespace CircuitBreaker.Contracts;

/// <summary>A read-only view of a circuit, for callers (to decide locally) and for the management UI.</summary>
public sealed record CircuitSnapshot
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public required CircuitState State { get; init; }

    /// <summary>Increments on every state change. Lets callers and the UI detect that something changed.</summary>
    public required long Generation { get; init; }

    public required DateTimeOffset StateChangedAt { get; init; }

    public DateTimeOffset? LastOutcomeAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public required int FailuresInWindow { get; init; }

    public required int SuccessesInWindow { get; init; }

    /// <summary>Failures in the window that did not count towards the threshold, such as functional errors.</summary>
    public required int IgnoredFailuresInWindow { get; init; }

    /// <summary>When the next probe is allowed, while open.</summary>
    public DateTimeOffset? RecoveryAt { get; init; }

    /// <summary>How often in a row the circuit reopened after a failed probe; drives the backoff.</summary>
    public required int ConsecutiveReopens { get; init; }

    public required int ActiveProbes { get; init; }

    public required int ProbeSuccesses { get; init; }

    public FailureSample? LastFailure { get; init; }

    public SwitchStatus? Switch { get; init; }

    /// <summary>Linked alert rules and the last poll. Null when the circuit links no alert rules.</summary>
    public AlertStatus? Alerts { get; init; }

    /// <summary>Whether the breaker can carry out what it decided for this circuit, and why not. Optional on the wire, so older breakers stay readable.</summary>
    public CircuitHealth Health { get; init; } = CircuitHealth.Healthy;

    /// <summary>The manual actions the current state allows. The UI shows exactly these.</summary>
    public required IReadOnlyList<CircuitAction> AllowedActions { get; init; }

    /// <summary>The most recent transitions, newest first.</summary>
    public required IReadOnlyList<TransitionRecord> History { get; init; }

    public required CircuitDefinition Definition { get; init; }
}

public sealed record TransitionRecord(CircuitState From, CircuitState To, string Trigger, DateTimeOffset At, string? Reason = null, string? Actor = null);

/// <summary>Where the switch should be, where it last was set, and whether that worked.</summary>
public sealed record SwitchStatus(
    SwitchKind Kind,
    string Target,
    SwitchPosition Desired,
    SwitchPosition? Applied,
    string? LastError,
    DateTimeOffset? LastAttemptAt,
    int FailedAttempts);

/// <summary>The result of polling the linked alert rules.</summary>
/// <param name="Mode">The polling mode in force at the last poll.</param>
/// <param name="Active">Whether at least one suitable rule fired at the last successful poll.</param>
/// <param name="WouldOpen">In shadow mode: whether the circuit would have opened.</param>
/// <param name="TrippedBy">The rules that opened (or would open) the circuit.</param>
/// <param name="Interval">The poll interval, so readers can tell a stale poller.</param>
public sealed record AlertStatus(
    AlertPollingMode Mode,
    bool Active,
    bool WouldOpen,
    IReadOnlyList<string> TrippedBy,
    IReadOnlyList<AlertRuleStatus> Rules,
    DateTimeOffset? LastPolledAt,
    DateTimeOffset? LastSucceededAt,
    string? LastError,
    TimeSpan Interval);

public sealed record AlertRuleStatus(string RuleId, AlertRuleSuitability Suitability, bool Firing);

/// <summary>A machine-readable reason with a stable code, so dashboards and alerts can match on it.</summary>
public sealed record HealthReason(int Code, string Message);

public sealed record CircuitHealth(CircuitHealthStatus Status, IReadOnlyList<HealthReason> Reasons)
{
    public static CircuitHealth Healthy { get; } = new(CircuitHealthStatus.Healthy, []);
}

/// <summary>The health of all circuits: the worst status, and every circuit that is not healthy.</summary>
public sealed record BreakerHealthReport(CircuitHealthStatus Status, int Circuits, IReadOnlyList<CircuitHealthEntry> Unhealthy);

public sealed record CircuitHealthEntry(string Key, CircuitState State, CircuitHealth Health);
