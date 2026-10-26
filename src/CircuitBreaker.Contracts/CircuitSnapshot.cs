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
