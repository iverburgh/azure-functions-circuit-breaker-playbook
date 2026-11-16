using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using CircuitBreaker.Function.Alerts;
using Microsoft.Extensions.Logging.Testing;
using static CircuitBreaker.Function.Tests.EntityHarness;

namespace CircuitBreaker.Function.Tests;

public sealed class AlertPollingTests
{
    private const string Rule = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-monitoring/providers/Microsoft.Insights/scheduledQueryRules/payments-errors";

    private static readonly SwitchDefinition FeatureFlag = new() { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "payments" };

    private EntityHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EntityHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    private int PendingPolls => _harness.Pending.Count(s => s.Operation == CircuitOperations.PollAlerts);

    private Task RegisterWithRuleAsync() =>
        _harness.RegisterAsync(Definition(d => d with { AlertRuleIds = [Rule], Switch = FeatureFlag }));

    [Test]
    public async Task Registering_a_circuit_with_linked_rules_starts_one_poll_loop_at_once()
    {
        await RegisterWithRuleAsync();

        Assert.That(PendingPolls, Is.EqualTo(1));
        Assert.That(_harness.Pending.Single(s => s.Operation == CircuitOperations.PollAlerts).At, Is.EqualTo(T0));
    }

    [Test]
    public async Task Registering_again_does_not_start_a_second_loop()
    {
        await RegisterWithRuleAsync();
        await RegisterWithRuleAsync();
        await _harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.That(PendingPolls, Is.EqualTo(1));
        Assert.That(_harness.AlertRules.Queries, Is.EqualTo(6), "One query at start-up and one per minute.");
    }

    [Test]
    public async Task A_firing_rule_opens_the_circuit_and_its_switch_and_a_person_closes_it_after_the_alert_resolves()
    {
        await RegisterWithRuleAsync();
        _harness.AlertRules.Firing(Rule, firing: true);

        await _harness.AdvanceAsync(TimeSpan.Zero);
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(_harness.State.Alerts!.TrippedBy, Is.EqualTo(new[] { Rule }));
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Open));

        var refused = await _harness.ActAsync(CircuitAction.Reset);
        Assert.That(refused.Accepted, Is.False, "No reset while the alert fires.");

        _harness.AlertRules.Firing(Rule, firing: false);
        await _harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.OpenedAfterAlert));
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Open), "The data may need repair first: the switch stays open.");

        var reset = await _harness.ActAsync(CircuitAction.Reset);
        Assert.That(reset.Accepted, Is.True);
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Closed));
    }

    [Test]
    public async Task A_poll_delivered_twice_queries_once()
    {
        await RegisterWithRuleAsync();
        var poll = _harness.Pending.Single(s => s.Operation == CircuitOperations.PollAlerts);
        _harness.Pending.Remove(poll);

        await _harness.RunAsync(poll.Operation, poll.Input);
        await _harness.RunAsync(poll.Operation, poll.Input);

        Assert.That(_harness.AlertRules.Queries, Is.EqualTo(1));
        Assert.That(PendingPolls, Is.EqualTo(1));
    }

    [Test]
    public async Task A_failed_query_changes_no_state_and_keeps_the_loop_going()
    {
        await RegisterWithRuleAsync();
        _harness.AlertRules.Firing(Rule, firing: true);
        await _harness.AdvanceAsync(TimeSpan.Zero);

        _harness.AlertRules.Fail = new AlertQueryException(AlertQueryFailure.Throttled, "Resource Graph returned 429.");
        await _harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.OpenedByAlert), "An outage of the monitoring plane does not close circuits.");
        Assert.That(_harness.State.Alerts!.LastError, Does.StartWith("Throttled"));
        Assert.That(PendingPolls, Is.EqualTo(1));
        Assert.That(_harness.Logger.Collector.GetSnapshot(), Has.Some.Matches<FakeLogRecord>(r => r.Id.Id == 1011));
    }

    [Test]
    public async Task With_polling_off_nothing_is_queried_but_the_loop_stays_ready_for_switching_it_on()
    {
        _harness.AlertOptions.Mode = AlertPollingMode.Off;
        await RegisterWithRuleAsync();
        _harness.AlertRules.Firing(Rule, firing: true);

        await _harness.AdvanceAsync(TimeSpan.FromMinutes(3));
        Assert.That(_harness.AlertRules.Queries, Is.Zero);
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed));

        _harness.AlertOptions.Mode = AlertPollingMode.Active;
        await _harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.OpenedByAlert));
    }

    [Test]
    public async Task In_shadow_mode_a_firing_rule_is_reported_but_does_not_open_the_circuit()
    {
        _harness.AlertOptions.Mode = AlertPollingMode.Shadow;
        await RegisterWithRuleAsync();
        _harness.AlertRules.Firing(Rule, firing: true);

        await _harness.AdvanceAsync(TimeSpan.Zero);

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(_harness.State.Alerts!.WouldOpen, Is.True);
        Assert.That(_harness.Switch.Position, Is.EqualTo(SwitchPosition.Closed));
    }

    [Test]
    public async Task Check_alerts_now_answers_with_the_new_state_without_starting_a_second_loop()
    {
        await RegisterWithRuleAsync();
        await _harness.AdvanceAsync(TimeSpan.Zero);
        _harness.AlertRules.Firing(Rule, firing: true);

        var snapshot = (CircuitSnapshot?)await _harness.RunAsync(CircuitOperations.PollAlertsNow);

        Assert.That(snapshot!.State, Is.EqualTo(CircuitState.OpenedByAlert));
        Assert.That(PendingPolls, Is.EqualTo(1));
    }

    [Test]
    public async Task Unlinking_all_rules_ends_the_loop_and_hands_an_alert_held_circuit_to_a_person()
    {
        await RegisterWithRuleAsync();
        _harness.AlertRules.Firing(Rule, firing: true);
        await _harness.AdvanceAsync(TimeSpan.Zero);

        await _harness.RegisterAsync(Definition(d => d with { Switch = FeatureFlag }));
        await _harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.OpenedAfterAlert));
        Assert.That(_harness.State.Alerts, Is.Null);
        Assert.That(PendingPolls, Is.Zero);
        Assert.That(_harness.AlertRules.Queries, Is.EqualTo(1));
    }

    [Test]
    public void The_health_report_lists_only_circuits_that_are_not_healthy_and_takes_the_worst_status()
    {
        var degraded = new CircuitHealth(CircuitHealthStatus.Degraded, [new HealthReason(CircuitHealthRules.PollerStale, "stale")]);
        var unhealthy = new CircuitHealth(CircuitHealthStatus.Unhealthy, [new HealthReason(CircuitHealthRules.SwitchNotApplied, "switch")]);

        var report = Http.CircuitApi.BuildHealthReport(
        [
            new("orders", CircuitState.Closed, CircuitHealth.Healthy),
            new("stock", CircuitState.Open, unhealthy),
            new("billing", CircuitState.Closed, degraded),
        ]);

        Assert.That(report.Status, Is.EqualTo(CircuitHealthStatus.Unhealthy));
        Assert.That(report.Circuits, Is.EqualTo(3));
        Assert.That(report.Unhealthy.Select(c => c.Key), Is.EqualTo(new[] { "billing", "stock" }));
    }

    [Test]
    public void An_empty_breaker_is_healthy()
    {
        var report = Http.CircuitApi.BuildHealthReport([]);

        Assert.That(report.Status, Is.EqualTo(CircuitHealthStatus.Healthy));
        Assert.That(report.Unhealthy, Is.Empty);
    }
}
