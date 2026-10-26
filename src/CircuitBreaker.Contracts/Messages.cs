using System.Text.Json.Serialization;

namespace CircuitBreaker.Contracts;

/// <summary>
/// Outcomes of calls through a circuit, aggregated by the client over a short interval. Counting
/// failures in a time window does not depend on their order, so a batch is as good as individual
/// reports — and keeps the load on the breaker independent of the call volume.
/// </summary>
public sealed record OutcomeReport
{
    public int Successes { get; init; }

    public IReadOnlyList<FailureCount> Failures { get; init; } = [];

    /// <summary>Results of probe calls made with a permit while half-open. Not included in <see cref="Successes"/> or <see cref="Failures"/>.</summary>
    public IReadOnlyList<ProbeResult> ProbeResults { get; init; } = [];

    /// <summary>The most recent failure in the batch, for operators.</summary>
    public FailureSample? LastFailure { get; init; }

    /// <summary>Which instance reported, for diagnostics.</summary>
    public string? Source { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Successes == 0 && Failures.Count == 0 && ProbeResults.Count == 0;
}

public sealed record FailureCount(FailureCategory Category, int Count);

/// <summary>
/// The result of a probe. A failure without a <see cref="Category"/> means the caller gave up before an
/// answer: the probe slot is released without a verdict.
/// </summary>
public sealed record ProbeResult(Guid ProbeId, bool Succeeded, FailureCategory? Category = null);

public sealed record FailureSample(FailureCategory Category, string Message, DateTimeOffset At);

public sealed record ProbeRequest(string? Source = null);

/// <summary>
/// The answer to "may I call the dependency now?". <see cref="Granted"/> with a <see cref="ProbeId"/>
/// is a probe permit: report its result with that id. <see cref="Granted"/> without one is a normal call.
/// </summary>
public sealed record ProbePermit(bool Granted, CircuitState State, Guid? ProbeId = null, DateTimeOffset? ExpiresAt = null, DateTimeOffset? RetryAfter = null);

public sealed record ManualActionRequest(string Reason);

public sealed record ManualActionResult(bool Accepted, string Message, CircuitSnapshot? Circuit = null);

public sealed record CallerIdentity(string? Name, IReadOnlyList<string> Roles);
