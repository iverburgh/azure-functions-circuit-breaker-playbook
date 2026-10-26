using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using static CircuitBreaker.Function.Tests.EntityHarness;

namespace CircuitBreaker.Function.Tests;

/// <summary>
/// Durable Entities are not replayed like orchestrations, but they are re-executed: after a crash the
/// runtime runs the operations since the last checkpoint again, and signals can arrive twice. These
/// tests prove the entity gives the same result either way.
/// </summary>
public sealed class ReplayAndConcurrencyTests
{
    private EntityHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EntityHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task An_operation_re_run_after_a_crash_ends_exactly_as_a_single_run()
    {
        var withSwitch = Definition(d => d with { Switch = new SwitchDefinition { Kind = SwitchKind.ServiceBusSubscription, Topic = "orders", Subscription = "payments" } });
        using var reference = new EntityHarness();
        await reference.RegisterAsync(withSwitch);
        await reference.ReportAsync(Failures(3));

        await _harness.RegisterAsync(withSwitch);
        await _harness.RunAsync(CircuitOperations.ReportOutcomes, Failures(3), crashBeforeCommit: true);
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed), "The crash lost the state change...");
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Open), "...but the switch had already been flipped.");

        await _harness.ReportAsync(Failures(3));

        Assert.That(_harness.PersistedJson, Is.EqualTo(reference.PersistedJson), "The re-run reaches the same state.");
        Assert.That(_harness.Switch.Writes, Is.EqualTo(2), "Closed on registration, open once: the repeated flip changed nothing.");
        Assert.That(_harness.Pending.Select(p => p.Operation), Is.EqualTo(reference.Pending.Select(p => p.Operation)), "No duplicate timers.");
    }

    [Test]
    public async Task A_timer_delivered_twice_transitions_once()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));
        var timer = _harness.Pending.Single(p => p.Operation == CircuitOperations.RecoveryTimeoutElapsed);
        _harness.Pending.Add(timer);

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.HalfOpen));
        Assert.That(_harness.State.History.Count(h => h.To == CircuitState.HalfOpen), Is.EqualTo(1));
    }

    [Test]
    public async Task A_timer_that_outlived_its_generation_is_ignored()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));
        await _harness.ActAsync(CircuitAction.Reset, "Fixed");
        await _harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        await _harness.ReportAsync(Failures(3));

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(25));

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Open), "The first opening's timer must not end the second opening early.");
    }

    [Test]
    public async Task Concurrent_probe_requests_never_exceed_the_limit()
    {
        await _harness.RegisterAsync(Definition(d => d with { HalfOpenMaxProbes = 3 }));
        await _harness.ReportAsync(Failures(3));
        await _harness.AdvanceAsync(TimeSpan.FromSeconds(30));

        var permits = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(_harness.AcquireProbeAsync)));

        Assert.That(permits.Count(p => p.Granted), Is.EqualTo(3));
        Assert.That(permits.Where(p => p.Granted).Select(p => p.ProbeId), Is.Unique);
    }

    [Test]
    public async Task Concurrent_reports_from_many_instances_are_all_counted()
    {
        await _harness.RegisterAsync(Definition(d => d with { FailureThreshold = 1_000 }));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            _harness.ReportAsync(new OutcomeReport { Successes = 10, Failures = [new FailureCount(FailureCategory.Timeout, 2)], Source = $"instance-{i}" }))));

        var totals = FailureWindow.Totals(_harness.State!, _harness.Time.GetUtcNow());
        Assert.That(totals.Successes, Is.EqualTo(400));
        Assert.That(totals.CountedFailures, Is.EqualTo(80));
    }

    [Test]
    public async Task A_switch_that_cannot_be_flipped_is_retried_without_losing_the_decision()
    {
        await _harness.RegisterAsync(Definition(d => d with
        {
            RecoveryTimeout = TimeSpan.FromMinutes(10),
            Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "payments" },
        }));
        _harness.Switch.FailNext = 2;

        await _harness.ReportAsync(Failures(3));

        var state = _harness.State!;
        Assert.That(state.State, Is.EqualTo(CircuitState.Open), "The state change is kept.");
        Assert.That(state.Switch!.Desired, Is.EqualTo(SwitchPosition.Open));
        Assert.That(state.Switch.Applied, Is.EqualTo(SwitchPosition.Closed));
        Assert.That(state.Switch.LastError, Is.EqualTo("Switch target unavailable"));
        Assert.That(state.Switch.RetryDueAt, Is.EqualTo(T0.AddSeconds(15)));

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(15));
        Assert.That(_harness.State!.Switch!.FailedAttempts, Is.EqualTo(2));
        Assert.That(_harness.State.Switch.RetryDueAt, Is.EqualTo(T0.AddSeconds(45)), "The retry delay doubles.");

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.That(_harness.State!.Switch!.Applied, Is.EqualTo(SwitchPosition.Open));
        Assert.That(_harness.State.Switch.LastError, Is.Null);
    }

    [Test]
    public async Task A_switch_that_failed_is_left_alone_when_the_circuit_returns_to_its_position()
    {
        await _harness.RegisterAsync(Definition(d => d with { Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "payments" } }));
        _harness.Switch.FailNext = 10;
        await _harness.ReportAsync(Failures(3));

        await _harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.HalfOpen), "Half-open wants the switch closed again...");
        Assert.That(_harness.State.Switch!.InSync, Is.True, "...which it still is: nothing left to do.");
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Closed));
    }

    [Test]
    public async Task A_superseded_switch_retry_does_nothing()
    {
        await _harness.RegisterAsync(Definition(d => d with { Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "payments" } }));
        _harness.Switch.FailNext = 1;
        await _harness.ReportAsync(Failures(3));
        var callsBefore = _harness.Switch.Calls;

        await _harness.RunAsync(CircuitOperations.ReconcileSwitch, T0.AddHours(1));

        Assert.That(_harness.Switch.Calls, Is.EqualTo(callsBefore));
    }
}
