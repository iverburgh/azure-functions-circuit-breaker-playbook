using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class RecoveryTests
{
    [Test]
    public void Opening_schedules_the_end_of_the_recovery_timeout()
    {
        var data = Registered();

        var effects = CircuitEngine.ReportOutcomes(data, Failures(3), T0);

        Assert.That(data.RecoveryAt, Is.EqualTo(T0.AddSeconds(30)));
        var signal = effects.Signals.Single(s => s.Operation == CircuitOperations.RecoveryTimeoutElapsed);
        Assert.That(signal.At, Is.EqualTo(data.RecoveryAt));
        Assert.That(signal.Input, Is.EqualTo(new RecoveryTimer(data.Generation)));
    }

    [Test]
    public void The_recovery_timeout_moves_the_circuit_to_half_open()
    {
        var data = Registered();
        Open(data);

        CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation), T0.AddSeconds(30));

        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen));
        Assert.That(data.RecoveryAt, Is.Null);
    }

    [Test]
    public void A_timer_from_an_earlier_generation_is_ignored()
    {
        // Open, reset by an operator, open again: the first timer must not half-open the second opening early.
        var data = Registered();
        Open(data);
        var firstTimer = new RecoveryTimer(data.Generation);
        CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "fixed", "ops"), T0.AddSeconds(5));
        Open(data, T0.AddSeconds(10));

        var effects = CircuitEngine.RecoveryTimeoutElapsed(data, firstTimer, T0.AddSeconds(30));

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
        Assert.That(effects.Ignored, Does.Contain("stale"));
    }

    [Test]
    public void A_timer_delivered_twice_acts_once()
    {
        var data = Registered();
        Open(data);
        var timer = new RecoveryTimer(data.Generation);

        CircuitEngine.RecoveryTimeoutElapsed(data, timer, T0.AddSeconds(30));
        var second = CircuitEngine.RecoveryTimeoutElapsed(data, timer, T0.AddSeconds(30));

        Assert.That(second.Transitions, Is.Empty);
        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen));
    }

    [Test]
    public void Every_failed_probe_doubles_the_recovery_timeout_up_to_the_maximum()
    {
        var data = Registered();
        var at = HalfOpen(data);
        var delays = new List<TimeSpan>();
        for (var round = 0; round < 6; round++)
        {
            var probe = AcquireProbe(data, at);
            CircuitEngine.ReportOutcomes(data, Probe(probe.ProbeId!.Value, succeeded: false, FailureCategory.Timeout), at);
            delays.Add(data.RecoveryAt!.Value - at);
            at = data.RecoveryAt.Value;
            CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation), at);
        }

        Assert.That(delays.Select(d => d.TotalSeconds), Is.EqualTo(new[] { 60, 120, 240, 240, 240, 240 }));
    }

    [Test]
    public void Closing_resets_the_backoff()
    {
        var data = Registered();
        var at = HalfOpen(data);
        var failed = AcquireProbe(data, at);
        CircuitEngine.ReportOutcomes(data, Probe(failed.ProbeId!.Value, succeeded: false, FailureCategory.Timeout), at);
        Assert.That(data.ConsecutiveReopens, Is.EqualTo(1));

        CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "fixed", "ops"), at);
        Open(data, at);

        Assert.That(data.ConsecutiveReopens, Is.Zero);
        Assert.That(data.RecoveryAt - at, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Jitter_spreads_recovery_within_bounds_and_is_deterministic()
    {
        var definition = Definition(d => d with { RecoveryJitter = 0.2 });
        var delays = Enumerable.Range(1, 200).Select(generation => RecoveryBackoff.Delay(definition, 0, generation)).ToList();

        Assert.That(delays, Has.All.InRange(TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36)));
        Assert.That(delays.Distinct().Count(), Is.GreaterThan(150), "Different generations spread out.");
        Assert.That(RecoveryBackoff.Delay(definition, 0, 7), Is.EqualTo(RecoveryBackoff.Delay(definition, 0, 7)));
    }

    [Test]
    public void Outcomes_reported_while_open_are_recorded_but_change_nothing()
    {
        var data = Registered();
        Open(data);
        var generation = data.Generation;

        var effects = CircuitEngine.ReportOutcomes(data, Failures(10), T0.AddSeconds(1));
        CircuitEngine.ReportOutcomes(data, Successes(10), T0.AddSeconds(2));

        Assert.That(effects.Transitions, Is.Empty);
        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
        Assert.That(data.Generation, Is.EqualTo(generation));
    }
}
