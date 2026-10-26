using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class HalfOpenProbeTests
{
    [Test]
    public void No_more_probes_are_handed_out_than_allowed()
    {
        var data = Registered();
        var at = HalfOpen(data);

        var permits = Enumerable.Range(0, 5).Select(_ => AcquireProbe(data, at)).ToList();

        Assert.That(permits.Count(p => p.Granted), Is.EqualTo(2));
        Assert.That(permits.Where(p => !p.Granted), Has.All.Matches<ProbePermit>(p => p.RetryAfter == at.AddSeconds(20)));
        Assert.That(data.Probes, Has.Count.EqualTo(2));
    }

    [Test]
    public void A_completed_probe_frees_its_slot()
    {
        var data = Registered(Definition(d => d with { HalfOpenMaxProbes = 1, HalfOpenSuccessThreshold = 3 }));
        var at = HalfOpen(data);
        var first = AcquireProbe(data, at);
        Assert.That(AcquireProbe(data, at).Granted, Is.False);

        CircuitEngine.ReportOutcomes(data, Probe(first.ProbeId!.Value, succeeded: true), at);

        Assert.That(AcquireProbe(data, at).Granted, Is.True);
    }

    [Test]
    public void Enough_successful_probes_close_the_circuit()
    {
        var data = Registered();
        var at = HalfOpen(data);
        var first = AcquireProbe(data, at);
        var second = AcquireProbe(data, at);

        CircuitEngine.ReportOutcomes(data, Probe(first.ProbeId!.Value, succeeded: true), at);
        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen), "One success is not enough.");

        var effects = CircuitEngine.ReportOutcomes(data, Probe(second.ProbeId!.Value, succeeded: true), at);

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(effects.Transitions.Single().Trigger, Is.EqualTo(nameof(CircuitTrigger.ProbesSucceeded)));
        Assert.That(FailureWindow.Totals(data, at).CountedFailures, Is.Zero, "Closing starts with a clean window.");
    }

    [Test]
    public void A_failed_probe_reopens_the_circuit()
    {
        var data = Registered();
        var at = HalfOpen(data);
        var probe = AcquireProbe(data, at);

        CircuitEngine.ReportOutcomes(data, Probe(probe.ProbeId!.Value, succeeded: false, FailureCategory.ServerError), at);

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
        Assert.That(data.Probes, Is.Empty);
    }

    [Test]
    public void A_functional_error_in_a_probe_proves_the_dependency_answers()
    {
        var data = Registered(Definition(d => d with { HalfOpenSuccessThreshold = 1 }));
        var at = HalfOpen(data);
        var probe = AcquireProbe(data, at);

        CircuitEngine.ReportOutcomes(data, Probe(probe.ProbeId!.Value, succeeded: false, FailureCategory.Functional), at);

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
    }

    [Test]
    public void A_probe_the_caller_gave_up_on_releases_its_slot_without_a_verdict()
    {
        var data = Registered(Definition(d => d with { HalfOpenMaxProbes = 1 }));
        var at = HalfOpen(data);
        var probe = AcquireProbe(data, at);

        CircuitEngine.ReportOutcomes(data, Probe(probe.ProbeId!.Value, succeeded: false, category: null), at);

        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen));
        Assert.That(data.ProbeSuccesses, Is.Zero);
        Assert.That(AcquireProbe(data, at).Granted, Is.True);
    }

    [Test]
    public void An_expired_lease_releases_its_slot_without_a_verdict()
    {
        var data = Registered(Definition(d => d with { HalfOpenMaxProbes = 1 }));
        var at = HalfOpen(data);
        var (probe, effects) = CircuitEngine.TryAcquireProbe(data, new ProbeRequest(), Guid.NewGuid(), at);
        var expiry = effects.Signals.Single(s => s.Operation == CircuitOperations.ProbeLeaseExpired);
        Assert.That(expiry.At, Is.EqualTo(probe.ExpiresAt));

        var result = CircuitEngine.ProbeLeaseExpired(data, (ProbeLeaseTimer)expiry.Input, expiry.At);

        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen));
        Assert.That(data.Probes, Is.Empty);
        Assert.That(result.Ignored, Does.Contain("Released 1"));
    }

    [Test]
    public void A_late_result_for_an_expired_probe_is_ignored()
    {
        var data = Registered(Definition(d => d with { HalfOpenSuccessThreshold = 1 }));
        var at = HalfOpen(data);
        var probe = AcquireProbe(data, at);
        CircuitEngine.ProbeLeaseExpired(data, new ProbeLeaseTimer(probe.ProbeId!.Value, data.Generation), probe.ExpiresAt!.Value);

        CircuitEngine.ReportOutcomes(data, Probe(probe.ProbeId.Value, succeeded: true), probe.ExpiresAt.Value.AddSeconds(1));

        Assert.That(data.State, Is.EqualTo(CircuitState.HalfOpen), "Only results for live leases count.");
    }

    [Test]
    public void A_failure_outside_a_probe_while_half_open_reopens_the_circuit()
    {
        var data = Registered();
        var at = HalfOpen(data);

        CircuitEngine.ReportOutcomes(data, Failures(1), at);

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
        Assert.That(data.History[0].Reason, Does.Contain("outside a probe"));
    }

    [Test]
    public void When_a_probe_success_and_an_outside_failure_arrive_together_the_failure_wins()
    {
        var data = Registered(Definition(d => d with { HalfOpenSuccessThreshold = 1 }));
        var at = HalfOpen(data);
        var probe = AcquireProbe(data, at);

        CircuitEngine.ReportOutcomes(data, Failures(1) with { ProbeResults = [new ProbeResult(probe.ProbeId!.Value, true)] }, at);

        Assert.That(data.State, Is.EqualTo(CircuitState.Open));
    }

    [TestCase(CircuitState.Closed, true, null)]
    [TestCase(CircuitState.Disabled, true, null)]
    [TestCase(CircuitState.Isolated, false, null)]
    public void Outside_half_open_no_probe_is_needed_or_none_is_possible(CircuitState state, bool granted, Guid? probeId)
    {
        var data = Registered();
        if (state != CircuitState.Closed)
        {
            CircuitEngine.ApplyManualAction(data, new ManualActionCommand(state == CircuitState.Isolated ? CircuitAction.Isolate : CircuitAction.Disable, "test", "ops"), T0);
        }

        var permit = AcquireProbe(data, T0);

        Assert.That(permit.Granted, Is.EqualTo(granted));
        Assert.That(permit.ProbeId, Is.EqualTo(probeId));
        Assert.That(permit.State, Is.EqualTo(state));
    }

    [Test]
    public void While_open_a_probe_is_refused_with_the_recovery_time()
    {
        var data = Registered();
        Open(data);

        var permit = AcquireProbe(data, T0.AddSeconds(1));

        Assert.That(permit.Granted, Is.False);
        Assert.That(permit.RetryAfter, Is.EqualTo(data.RecoveryAt));
    }
}
