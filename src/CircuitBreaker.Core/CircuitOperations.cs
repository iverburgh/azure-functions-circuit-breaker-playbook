using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core;

/// <summary>Names of the entity operations, shared by the engine (which schedules them) and the entity (which implements them).</summary>
public static class CircuitOperations
{
    public const string Register = nameof(Register);
    public const string ReportOutcomes = nameof(ReportOutcomes);
    public const string TryAcquireProbe = nameof(TryAcquireProbe);
    public const string ApplyManualAction = nameof(ApplyManualAction);
    public const string RecoveryTimeoutElapsed = nameof(RecoveryTimeoutElapsed);
    public const string ProbeLeaseExpired = nameof(ProbeLeaseExpired);
    public const string ReconcileSwitch = nameof(ReconcileSwitch);
    public const string CheckStaleness = nameof(CheckStaleness);
}

public sealed record RecoveryTimer(long Generation);

public sealed record ProbeLeaseTimer(Guid ProbeId, long Generation);

public sealed record ManualActionCommand(CircuitAction Action, string Reason, string Actor);

/// <summary>A signal the entity should send to itself at a later time.</summary>
public sealed record ScheduledSignal(string Operation, object Input, DateTimeOffset At);

/// <summary>What an operation caused, for the entity to carry out: scheduled signals, and the transitions to log and measure.</summary>
public sealed class CircuitEffects
{
    public List<TransitionRecord> Transitions { get; } = [];

    public List<ScheduledSignal> Signals { get; } = [];

    /// <summary>Set when the operation was recognised as stale or duplicate and changed nothing.</summary>
    public string? Ignored { get; set; }
}
