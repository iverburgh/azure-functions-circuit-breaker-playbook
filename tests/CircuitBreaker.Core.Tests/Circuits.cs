using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core.Tests;

internal static class Circuits
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static CircuitDefinition Definition(Func<CircuitDefinition, CircuitDefinition>? customize = null)
    {
        var definition = new CircuitDefinition
        {
            Key = "payments-api",
            DisplayName = "Payments API",
            FailureThreshold = 3,
            FailureWindow = TimeSpan.FromSeconds(60),
            RecoveryTimeout = TimeSpan.FromSeconds(30),
            MaxRecoveryTimeout = TimeSpan.FromMinutes(4),
            RecoveryJitter = 0,
            HalfOpenMaxProbes = 2,
            HalfOpenSuccessThreshold = 2,
            ProbeTimeout = TimeSpan.FromSeconds(20),
        };
        return customize is null ? definition : customize(definition);
    }

    public static CircuitData Registered(CircuitDefinition? definition = null, DateTimeOffset? at = null)
    {
        var data = new CircuitData();
        CircuitEngine.Register(data, definition ?? Definition(), at ?? T0);
        return data;
    }

    public static OutcomeReport Failures(int count, FailureCategory category = FailureCategory.ServerError) =>
        new() { Failures = [new FailureCount(category, count)] };

    public static OutcomeReport Successes(int count) => new() { Successes = count };

    public static OutcomeReport Probe(Guid probeId, bool succeeded, FailureCategory? category = null) =>
        new() { ProbeResults = [new ProbeResult(probeId, succeeded, category)] };

    /// <summary>Opens the circuit and returns the time at which it opened.</summary>
    public static DateTimeOffset Open(CircuitData data, DateTimeOffset? at = null)
    {
        var when = at ?? T0;
        CircuitEngine.ReportOutcomes(data, Failures(data.RequiredDefinition.FailureThreshold), when);
        Assert.That(data.State, Is.EqualTo(CircuitState.Open), "Precondition: the circuit opened.");
        return when;
    }

    /// <summary>Opens the circuit and lets the recovery timeout elapse; returns the time of half-opening.</summary>
    public static DateTimeOffset HalfOpen(CircuitData data, DateTimeOffset? at = null)
    {
        Open(data, at);
        var recoveryAt = data.RecoveryAt!.Value;
        CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation), recoveryAt);
        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen), "Precondition: the circuit is half-open.");
        return recoveryAt;
    }

    public static ProbePermit AcquireProbe(CircuitData data, DateTimeOffset now) =>
        CircuitEngine.TryAcquireProbe(data, new ProbeRequest("test"), Guid.NewGuid(), now).Permit;
}
