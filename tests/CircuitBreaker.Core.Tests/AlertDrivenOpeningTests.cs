using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class AlertDrivenOpeningTests
{
    [Test]
    public void A_firing_alert_rule_opens_a_closed_circuit_and_records_which_rule_tripped_it()
    {
        var data = WithAlertRules(Rule);

        var effects = Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(data.Alerts!.TrippedBy, Is.EqualTo(new[] { Rule }));
        Assert.That(effects.Transitions.Single().Reason, Does.Contain("payments-errors"));
        Assert.That(data.RecoveryAt, Is.Null, "An alert-held circuit has no recovery timer: the alert decides.");
    }

    [Test]
    public void An_alert_overrides_an_open_circuit_and_its_pending_recovery_timer_becomes_stale()
    {
        var data = WithAlertRules(Rule);
        Open(data);
        var timer = new RecoveryTimer(data.Generation);

        Poll(data, T0.AddSeconds(5), AlertPollingMode.Active, RuleInfo(firing: true));
        var late = CircuitEngine.RecoveryTimeoutElapsed(data, timer, T0.AddMinutes(1));

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(late.Ignored, Is.Not.Null);
    }

    [Test]
    public void While_the_alert_fires_the_circuit_cannot_be_reset_or_probed_and_outcomes_change_nothing()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var reset = CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "try", "operator"), T0.AddSeconds(1));
        CircuitEngine.ReportOutcomes(data, Successes(100), T0.AddSeconds(2));
        var permit = AcquireProbe(data, T0.AddSeconds(3));

        Assert.That(reset.Accepted, Is.False);
        Assert.That(permit.Granted, Is.False);
        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
    }

    [Test]
    public void Disable_is_the_emergency_release_while_the_alert_still_fires()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var disable = CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Disable, "release on purpose", "operator"), T0.AddSeconds(1));
        Poll(data, T0.AddMinutes(1), AlertPollingMode.Active, RuleInfo(firing: true));

        Assert.That(disable.Accepted, Is.True);
        Assert.That(data.State, Is.EqualTo(CircuitState.Disabled), "Disabled is a person's decision and ignores the alert.");
    }

    [Test]
    public void A_resolved_alert_hands_the_circuit_to_a_person_instead_of_closing_it()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        Poll(data, T0.AddMinutes(20), AlertPollingMode.Active, RuleInfo(firing: false));
        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedAfterAlert));
        Assert.That(data.Alerts!.Active, Is.False);

        var reset = CircuitEngine.ApplyManualAction(data, new ManualActionCommand(CircuitAction.Reset, "data repaired", "operator"), T0.AddMinutes(30));
        Assert.That(reset.Accepted, Is.True);
        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
    }

    [Test]
    public void An_alert_that_fires_again_before_the_reset_takes_the_circuit_back()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));
        Poll(data, T0.AddMinutes(20), AlertPollingMode.Active, RuleInfo(firing: false));

        Poll(data, T0.AddMinutes(25), AlertPollingMode.Active, RuleInfo(firing: true));

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
    }

    [TestCase(CircuitAction.Isolate, CircuitState.Isolated)]
    [TestCase(CircuitAction.Disable, CircuitState.Disabled)]
    public void Manual_overrides_ignore_alerts(CircuitAction action, CircuitState expected)
    {
        var data = WithAlertRules(Rule);
        CircuitEngine.ApplyManualAction(data, new ManualActionCommand(action, "maintenance", "operator"), T0);

        var effects = Poll(data, T0.AddMinutes(1), AlertPollingMode.Active, RuleInfo(firing: true));

        Assert.That(data.State, Is.EqualTo(expected));
        Assert.That(effects.Transitions, Is.Empty);
        Assert.That(data.Alerts!.Active, Is.True, "The alert is still shown, so the operator knows what a reset would run into.");
    }

    [Test]
    public void Any_one_firing_rule_is_enough()
    {
        var data = WithAlertRules(Rule, MetricRule);

        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: false), RuleInfo(firing: true, ruleId: MetricRule));

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(data.Alerts!.TrippedBy, Is.EqualTo(new[] { MetricRule }));
    }

    [Test]
    public void Shadow_mode_shows_what_would_happen_without_changing_the_state()
    {
        var data = WithAlertRules(Rule);

        var effects = Poll(data, T0, AlertPollingMode.Shadow, RuleInfo(firing: true));

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(effects.Transitions, Is.Empty);
        Assert.That(data.Alerts!.WouldOpen, Is.True);
        Assert.That(data.Alerts.Active, Is.False);
    }

    [TestCase(AlertPollingMode.Off)]
    [TestCase(AlertPollingMode.Shadow)]
    public void Turning_active_polling_back_hands_alert_held_circuits_to_a_person(AlertPollingMode mode)
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var settings = new AlertPollSettings(mode, TimeSpan.FromMinutes(1), Jitter: 0);
        var result = mode == AlertPollingMode.Off ? null : AlertPollResult.Success([RuleInfo(firing: true)]);
        var effects = CircuitEngine.ApplyAlertPoll(data, settings, result, T0.AddMinutes(1), scheduled: true);

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedAfterAlert), "Never straight to Closed: a person decides.");
        Assert.That(effects.Signals.Single().Operation, Is.EqualTo(CircuitOperations.PollAlerts), "The loop keeps running, so turning polling on again needs no restart.");
    }

    [Test]
    public void Polling_off_queries_nothing()
    {
        var data = WithAlertRules(Rule);

        Assert.That(CircuitEngine.ShouldQueryAlerts(data, new AlertPollSettings(AlertPollingMode.Off, TimeSpan.FromMinutes(1))), Is.False);
        Assert.That(CircuitEngine.ShouldQueryAlerts(data, new AlertPollSettings(AlertPollingMode.Shadow, TimeSpan.FromMinutes(1))), Is.True);
        Assert.That(CircuitEngine.ShouldQueryAlerts(Registered(), new AlertPollSettings(AlertPollingMode.Active, TimeSpan.FromMinutes(1))), Is.False);
    }

    [Test]
    public void A_failed_query_records_the_error_and_changes_no_state()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var settings = new AlertPollSettings(AlertPollingMode.Active, TimeSpan.FromMinutes(1), Jitter: 0);
        var effects = CircuitEngine.ApplyAlertPoll(data, settings, AlertPollResult.Failure("Throttled: 429"), T0.AddMinutes(1), scheduled: true);

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert), "An outage of the monitoring plane must not close circuits.");
        Assert.That(data.Alerts!.LastError, Is.EqualTo("Throttled: 429"));
        Assert.That(data.Alerts.LastSucceededAt, Is.EqualTo(T0));
        Assert.That(effects.Signals, Has.Count.EqualTo(1));
    }

    [TestCase("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg/providers/Microsoft.Insights/activityLogAlerts/x", true, true, true, AlertRuleSuitability.UnsupportedType)]
    [TestCase(Rule, false, true, true, AlertRuleSuitability.NotFound)]
    [TestCase(Rule, true, false, false, AlertRuleSuitability.Stateless)]
    [TestCase(Rule, true, true, false, AlertRuleSuitability.Disabled)]
    [TestCase(MetricRule, true, true, true, AlertRuleSuitability.Suitable)]
    public void Suitability_reports_the_first_problem(string ruleId, bool found, bool autoMitigate, bool enabled, AlertRuleSuitability expected)
    {
        var info = found ? RuleInfo(firing: false, ruleId, enabled, autoMitigate) : null;

        Assert.That(AlertEvaluator.Suitability(ruleId, info), Is.EqualTo(expected));
    }

    [Test]
    public void Only_suitable_rules_can_open_the_circuit()
    {
        var data = WithAlertRules(Rule);

        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true, autoMitigate: false));

        Assert.That(data.State, Is.EqualTo(CircuitState.Closed), "A stateless rule would hold the circuit open forever.");
        Assert.That(data.Alerts!.Rules.Single(), Is.EqualTo(new AlertRuleStatus(Rule, AlertRuleSuitability.Stateless, Firing: true)));
    }

    [Test]
    public void Rule_ids_match_regardless_of_case()
    {
        var data = WithAlertRules(Rule);

        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true) with { RuleId = Rule.ToUpperInvariant() });

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
    }

    [Test]
    public void Registration_starts_the_poll_loop_once()
    {
        var data = new CircuitData();
        var definition = Definition(d => d with { AlertRuleIds = [Rule] });

        var first = CircuitEngine.Register(data, definition, T0);
        var second = CircuitEngine.Register(data, definition, T0.AddSeconds(10));

        Assert.That(first.Signals.Where(s => s.Operation == CircuitOperations.PollAlerts).Select(s => s.At), Is.EqualTo(new[] { T0 }));
        Assert.That(second.Signals, Is.Empty);
        Assert.That(CircuitEngine.IsAlertPollDue(data, T0), Is.True);
        Assert.That(CircuitEngine.IsAlertPollDue(data, T0.AddSeconds(1)), Is.False, "Another due time is a duplicate.");
    }

    [Test]
    public void Registration_restarts_a_poll_loop_that_was_lost()
    {
        var data = WithAlertRules(Rule);

        var effects = CircuitEngine.Register(data, data.RequiredDefinition, T0.AddHours(1));

        Assert.That(effects.Signals.Single().Operation, Is.EqualTo(CircuitOperations.PollAlerts));
        Assert.That(data.Alerts!.NextPollAt, Is.EqualTo(T0.AddHours(1)));
    }

    [Test]
    public void A_scheduled_poll_schedules_the_next_one_within_the_jittered_interval()
    {
        var data = WithAlertRules(Rule);
        var settings = new AlertPollSettings(AlertPollingMode.Active, TimeSpan.FromMinutes(1), Jitter: 0.2);

        var effects = CircuitEngine.ApplyAlertPoll(data, settings, AlertPollResult.Success([RuleInfo(firing: false)]), T0, scheduled: true);

        var next = effects.Signals.Single();
        Assert.That(next.At, Is.InRange(T0.AddSeconds(48), T0.AddSeconds(72)));
        Assert.That(next.Input, Is.EqualTo(next.At), "The due time is the token that recognises duplicates.");
        Assert.That(data.Alerts!.NextPollAt, Is.EqualTo(next.At));
    }

    [Test]
    public void An_on_demand_check_does_not_start_a_second_loop()
    {
        var data = WithAlertRules(Rule);
        var settings = new AlertPollSettings(AlertPollingMode.Active, TimeSpan.FromMinutes(1));

        var effects = CircuitEngine.ApplyAlertPoll(data, settings, AlertPollResult.Success([RuleInfo(firing: true)]), T0.AddSeconds(5), scheduled: false);

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(effects.Signals, Is.Empty);
    }

    [Test]
    public void Unlinking_every_rule_ends_the_loop_and_hands_an_alert_held_circuit_to_a_person()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var effects = CircuitEngine.Register(data, Definition(), T0.AddMinutes(1));

        Assert.That(data.State, Is.EqualTo(CircuitState.OpenedAfterAlert));
        Assert.That(effects.Transitions, Has.Count.EqualTo(1));
        Assert.That(data.Alerts, Is.Null);
        Assert.That(CircuitEngine.IsAlertPollDue(data, T0.AddMinutes(1)), Is.False, "The pending poll is ignored when it arrives.");
    }

    [Test]
    public void Invalid_alert_rule_ids_are_refused()
    {
        Assert.That(Definition(d => d with { AlertRuleIds = ["payments-errors"] }).Validate(), Has.Some.Contains("resource ID"));
        Assert.That(Definition(d => d with { AlertRuleIds = [Rule + "' or 1==1"] }).Validate(), Has.Some.Contains("resource ID"));
        Assert.That(Definition(d => d with { AlertRuleIds = [Rule, Rule.ToUpperInvariant()] }).Validate(), Has.Some.Contains("twice"));
        Assert.That(Definition(d => d with { AlertRuleIds = [.. Enumerable.Range(0, 21).Select(i => $"{Rule}{i}")] }).Validate(), Has.Some.Contains("at most"));
        Assert.That(Definition(d => d with { AlertRuleIds = [Rule, MetricRule] }).Validate(), Is.Empty);
    }

    [Test]
    public void The_snapshot_shows_the_alert_status()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        var snapshot = CircuitEngine.ToSnapshot(data, T0);

        Assert.That(snapshot.Alerts, Is.Not.Null);
        Assert.That(snapshot.Alerts!.Mode, Is.EqualTo(AlertPollingMode.Active));
        Assert.That(snapshot.Alerts.TrippedBy, Is.EqualTo(new[] { Rule }));
        Assert.That(snapshot.AllowedActions, Does.Not.Contain(CircuitAction.Reset));
        Assert.That(CircuitEngine.ToSnapshot(Registered(), T0).Alerts, Is.Null);
    }
}
