using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class ManualActionTests
{
    [Test]
    public void An_isolated_circuit_never_recovers_on_its_own()
    {
        var data = Registered();
        Open(data);
        var timer = new RecoveryTimer(data.Generation);

        CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Isolate, "Dependency in maintenance", "ops"), T0.AddSeconds(1));
        CircuitEngine.RecoveryTimeoutElapsed(data, timer, T0.AddSeconds(30));
        CircuitEngine.ReportOutcomes(data, Successes(100), T0.AddSeconds(31));

        Assert.That(data.State, Is.EqualTo(CircuitState.Isolated));
    }

    [Test]
    public void A_disabled_circuit_lets_everything_through_whatever_fails()
    {
        var data = Registered();
        CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Disable, "Breaker misbehaving", "ops"), T0);

        CircuitEngine.ReportOutcomes(data, Failures(100), T0);

        Assert.That(data.State, Is.EqualTo(CircuitState.Disabled));
        Assert.That(FailureWindow.Totals(data, T0).CountedFailures, Is.EqualTo(100), "Outcomes are still visible.");
    }

    [Test]
    public void Reset_closes_the_circuit_with_a_clean_slate()
    {
        var data = Registered();
        Open(data);

        var (accepted, message, effects) = CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "Fixed upstream", "alice"), T0.AddSeconds(5));

        Assert.That(accepted, Is.True);
        Assert.That(message, Does.Contain("Open to Closed"));
        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(FailureWindow.Totals(data, T0.AddSeconds(5)).CountedFailures, Is.Zero);
        Assert.That(effects.Transitions.Single(), Is.EqualTo(new TransitionRecord(CircuitState.Open, CircuitState.Closed, "Reset", T0.AddSeconds(5), "Fixed upstream", "alice")));
    }

    [Test]
    public void An_action_the_state_does_not_allow_is_refused_and_changes_nothing()
    {
        var data = Registered();
        var generation = data.Generation;

        var (accepted, message, effects) = CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "test", "ops"), T0);

        Assert.That(accepted, Is.False);
        Assert.That(message, Does.Contain("not allowed while the circuit is Closed"));
        Assert.That(effects.Transitions, Is.Empty);
        Assert.That(data.Generation, Is.EqualTo(generation));
    }

    [Test]
    public void The_history_records_who_did_what_and_why_newest_first_and_bounded()
    {
        var data = Registered();
        for (var i = 0; i < 30; i++)
        {
            CircuitEngine.ApplyManualAction(data, new ManualActionCommand(i % 2 == 0 ? CircuitAction.Isolate : CircuitAction.Reset, $"round {i}", "ops"), T0.AddMinutes(i));
        }

        Assert.That(data.History, Has.Count.EqualTo(CircuitData.MaxHistory));
        Assert.That(data.History[0].Reason, Is.EqualTo("round 29"));
        Assert.That(data.History[0].Actor, Is.EqualTo("ops"));
    }

    [Test]
    public void The_switch_follows_the_state()
    {
        var data = Registered(Definition(d => d with { Switch = new SwitchDefinition { Kind = SwitchKind.ServiceBusSubscription, Topic = "orders", Subscription = "payments" } }));
        Assert.That(data.Switch!.Desired, Is.EqualTo(SwitchPosition.Closed));

        Open(data);
        Assert.That(data.Switch.Desired, Is.EqualTo(SwitchPosition.Open));

        CircuitEngine.RecoveryTimeoutElapsed(data, new RecoveryTimer(data.Generation), data.RecoveryAt!.Value);
        Assert.That(data.Switch.Desired, Is.EqualTo(SwitchPosition.Closed), "Half-open lets work through to find out whether it recovered.");
    }

    [Test]
    public void Changing_the_switch_target_starts_its_status_afresh()
    {
        var first = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "a" };
        var data = Registered(Definition(d => d with { Switch = first }));
        data.Switch!.Applied = SwitchPosition.Closed;

        CircuitEngine.Register(data, Definition(d => d with { Switch = first with { FeatureFlag = "b" } }), T0);

        Assert.That(data.Switch.Target, Is.EqualTo("main/b"));
        Assert.That(data.Switch.Applied, Is.Null);
    }
}

public sealed class StalenessTests
{
    [Test]
    public void Registering_schedules_one_staleness_check()
    {
        var data = new CircuitData();

        var first = CircuitEngine.Register(data, Definition(), T0);
        var again = CircuitEngine.Register(data, Definition(), T0.AddMinutes(5));

        Assert.That(first.Signals.Single(s => s.Operation == CircuitOperations.CheckStaleness).At, Is.EqualTo(T0.AddDays(1)));
        Assert.That(again.Signals, Is.Empty, "A check is already pending.");
    }

    [Test]
    public void A_circuit_nobody_uses_any_more_is_deleted()
    {
        var data = Registered();
        var dueAt = data.NextStalenessCheckAt!.Value;
        var (delete, _) = CircuitEngine.CheckStaleness(data, dueAt, T0.AddDays(8));

        Assert.That(delete, Is.True);
    }

    [Test]
    public void A_circuit_in_use_is_kept_and_checked_again_later()
    {
        var data = Registered();
        var dueAt = data.NextStalenessCheckAt!.Value;
        CircuitEngine.ReportOutcomes(data, Successes(1), dueAt.AddMinutes(-1));

        var (delete, effects) = CircuitEngine.CheckStaleness(data, dueAt, dueAt);

        Assert.That(delete, Is.False);
        Assert.That(data.NextStalenessCheckAt, Is.EqualTo(dueAt.AddDays(1)));
        Assert.That(effects.Signals.Single().Input, Is.EqualTo(dueAt.AddDays(1)));
    }

    [Test]
    public void A_superseded_check_is_ignored()
    {
        var data = Registered();

        var (delete, effects) = CircuitEngine.CheckStaleness(data, T0.AddHours(3), T0.AddDays(30));

        Assert.That(delete, Is.Null);
        Assert.That(effects.Ignored, Does.Contain("superseded"));
    }
}
