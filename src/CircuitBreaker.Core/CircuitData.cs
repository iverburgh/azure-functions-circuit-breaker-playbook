using System.Text.Json.Serialization;
using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core;

/// <summary>
/// The durable state of one circuit, stored by the Durable Entity. Everything the breaker decides
/// on lives here — counters, timestamps, probe leases — so a restart, a scale-out or a crash loses
/// nothing. Plain properties only: it is serialized as JSON after every operation.
/// </summary>
public sealed class CircuitData
{
    public const int MaxHistory = 20;

    public CircuitDefinition? Definition { get; set; }

    public CircuitState State { get; set; } = CircuitState.Closed;

    /// <summary>Increments on every transition. Scheduled signals carry it, so stale ones are recognised and ignored.</summary>
    public long Generation { get; set; }

    public DateTimeOffset StateChangedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? LastOutcomeAt { get; set; }

    public List<WindowBucket> Window { get; set; } = [];

    public int ConsecutiveReopens { get; set; }

    public DateTimeOffset? RecoveryAt { get; set; }

    public List<ProbeLease> Probes { get; set; } = [];

    public int ProbeSuccesses { get; set; }

    public FailureSample? LastFailure { get; set; }

    public SwitchState? Switch { get; set; }

    /// <summary>Newest first, at most <see cref="MaxHistory"/>.</summary>
    public List<TransitionRecord> History { get; set; } = [];

    /// <summary>Due time of the single pending staleness check; a check with another due time is a duplicate.</summary>
    public DateTimeOffset? NextStalenessCheckAt { get; set; }

    [JsonIgnore]
    public CircuitDefinition RequiredDefinition =>
        Definition ?? throw new InvalidOperationException("The circuit has not been registered.");
}

/// <summary>Outcome counts for one slice of the failure window.</summary>
public sealed class WindowBucket
{
    public DateTimeOffset Start { get; set; }

    public int Successes { get; set; }

    public int CountedFailures { get; set; }

    public int IgnoredFailures { get; set; }
}

/// <summary>A probe permit handed out while half-open. It expires if no result comes back in time.</summary>
public sealed class ProbeLease
{
    public Guid Id { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? Source { get; set; }
}

public sealed class SwitchState
{
    public string Target { get; set; } = string.Empty;

    public SwitchPosition Desired { get; set; }

    public SwitchPosition? Applied { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>Due time of the single pending retry; a retry signal with another due time is a duplicate.</summary>
    public DateTimeOffset? RetryDueAt { get; set; }

    [JsonIgnore]
    public bool InSync => Applied == Desired;
}

public readonly record struct WindowTotals(int Successes, int CountedFailures, int IgnoredFailures);
