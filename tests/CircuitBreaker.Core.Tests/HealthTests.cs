using CircuitBreaker.Contracts;
using static CircuitBreaker.Core.Tests.Circuits;

namespace CircuitBreaker.Core.Tests;

public sealed class HealthTests
{
    private static readonly AlertPollSettings Active = new(AlertPollingMode.Active, TimeSpan.FromMinutes(1), Jitter: 0);

    [Test]
    public void A_circuit_without_problems_is_healthy()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: true));

        Assert.That(CircuitEngine.Health(data, T0.AddMinutes(1)), Is.EqualTo(CircuitHealth.Healthy), "An open circuit is a decision, not a health problem.");
    }

    [Test]
    public void A_poller_that_has_not_succeeded_for_five_intervals_is_stale()
    {
        var data = WithAlertRules(Rule);
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: false));
        CircuitEngine.ApplyAlertPoll(data, Active, AlertPollResult.Failure("Unauthorized: 403"), T0.AddMinutes(6), scheduled: true);

        Assert.That(CircuitEngine.Health(data, T0.AddMinutes(5)).Status, Is.EqualTo(CircuitHealthStatus.Healthy));
        var health = CircuitEngine.Health(data, T0.AddMinutes(6));
        Assert.That(health.Status, Is.EqualTo(CircuitHealthStatus.Degraded));
        Assert.That(health.Reasons.Single().Code, Is.EqualTo(CircuitHealthRules.PollerStale));
        Assert.That(health.Reasons.Single().Message, Does.Contain("403"));
    }

    [Test]
    public void A_poller_that_never_succeeded_is_stale_counted_from_when_it_started()
    {
        var data = WithAlertRules(Rule);
        CircuitEngine.ApplyAlertPoll(data, Active, AlertPollResult.Failure("Network"), T0, scheduled: true);

        Assert.That(CircuitEngine.Health(data, T0.AddMinutes(6)).Reasons.Single().Code, Is.EqualTo(CircuitHealthRules.PollerStale));
    }

    [Test]
    public void Polling_off_is_never_stale()
    {
        var data = WithAlertRules(Rule);
        CircuitEngine.ApplyAlertPoll(data, Active with { Mode = AlertPollingMode.Off }, null, T0, scheduled: true);

        Assert.That(CircuitEngine.Health(data, T0.AddDays(1)), Is.EqualTo(CircuitHealth.Healthy));
    }

    [Test]
    public void Unusable_rules_each_have_their_own_code()
    {
        var data = WithAlertRules(Rule, MetricRule, "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg/providers/Microsoft.Insights/activityLogAlerts/x");
        Poll(data, T0, AlertPollingMode.Active, RuleInfo(firing: false, autoMitigate: false));

        var health = CircuitEngine.Health(data, T0);

        Assert.That(health.Status, Is.EqualTo(CircuitHealthStatus.Degraded));
        Assert.That(health.Reasons.Select(r => r.Code), Is.EquivalentTo(new[]
        {
            CircuitHealthRules.RuleStateless, CircuitHealthRules.RuleNotFound, CircuitHealthRules.RuleUnsupportedType,
        }));
    }

    [TestCase(1, CircuitHealthStatus.Degraded)]
    [TestCase(2, CircuitHealthStatus.Degraded)]
    [TestCase(3, CircuitHealthStatus.Unhealthy)]
    public void A_switch_that_keeps_failing_makes_the_circuit_unhealthy(int failedAttempts, CircuitHealthStatus expected)
    {
        var data = Registered(Definition(d => d with { Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "appcs-main", FeatureFlag = "Payments" } }));
        Open(data);
        data.Switch!.FailedAttempts = failedAttempts;
        data.Switch.LastError = "Forbidden";

        var health = CircuitEngine.Health(data, T0);

        Assert.That(health.Status, Is.EqualTo(expected), "The decision to stop work is not enforced.");
        Assert.That(health.Reasons.Single().Code, Is.EqualTo(CircuitHealthRules.SwitchNotApplied));
    }

    [Test]
    public void The_snapshot_carries_the_health()
    {
        var data = WithAlertRules(Rule);
        CircuitEngine.ApplyAlertPoll(data, Active, AlertPollResult.Failure("Throttled"), T0, scheduled: true);

        Assert.That(CircuitEngine.ToSnapshot(data, T0.AddHours(1)).Health.Status, Is.EqualTo(CircuitHealthStatus.Degraded));
    }
}
