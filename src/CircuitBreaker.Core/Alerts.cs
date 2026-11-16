using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core;

/// <summary>An alert rule as Azure Resource Graph reports it, with the IDs of its alerts that currently fire.</summary>
/// <param name="RuleId">The resource ID of the rule, in lower case.</param>
/// <param name="AutoMitigate">Whether the rule resolves its alerts automatically (stateful).</param>
public sealed record AlertRuleInfo(string RuleId, bool Enabled, bool AutoMitigate, IReadOnlyList<string> FiredAlertIds);

/// <summary>The alert polling configuration, as the entity passes it to the engine.</summary>
/// <param name="Jitter">Random spread of the interval, as a fraction (0.2 = ±20%), so circuits do not poll in step.</param>
public sealed record AlertPollSettings(AlertPollingMode Mode, TimeSpan Interval, double Jitter = 0.2);

/// <summary>The result of one alert query: the rules that were found, or why the query failed.</summary>
public sealed record AlertPollResult(IReadOnlyList<AlertRuleInfo>? Rules, string? Error)
{
    public static AlertPollResult Success(IReadOnlyList<AlertRuleInfo> rules) => new(rules, null);

    public static AlertPollResult Failure(string error) => new(null, error);
}

/// <summary>Pure rules for linked alert rules: which ones may drive a circuit, and whether they fire.</summary>
public static class AlertEvaluator
{
    /// <summary>Only these rule types resolve their alerts on their own (when stateful).</summary>
    public static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft.insights/scheduledqueryrules",
        "microsoft.insights/metricalerts",
    };

    public static (IReadOnlyList<AlertRuleStatus> Rules, IReadOnlyList<string> TrippedBy) Evaluate(
        IReadOnlyList<string> linkedRuleIds, IReadOnlyList<AlertRuleInfo> found)
    {
        var byId = found
            .GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rules = new List<AlertRuleStatus>();
        var trippedBy = new List<string>();
        foreach (var ruleId in linkedRuleIds)
        {
            byId.TryGetValue(ruleId, out var info);
            var suitability = Suitability(ruleId, info);
            var firing = info is not null && info.FiredAlertIds.Count > 0;
            rules.Add(new AlertRuleStatus(ruleId, suitability, firing));
            if (suitability == AlertRuleSuitability.Suitable && firing)
            {
                trippedBy.Add(ruleId);
            }
        }

        return (rules, trippedBy);
    }

    /// <summary>The first problem wins: type, then existence, then statefulness, then enabled.</summary>
    public static AlertRuleSuitability Suitability(string ruleId, AlertRuleInfo? info) => true switch
    {
        _ when !SupportedTypes.Contains(TypeOf(ruleId)) => AlertRuleSuitability.UnsupportedType,
        _ when info is null => AlertRuleSuitability.NotFound,
        _ when !info.AutoMitigate => AlertRuleSuitability.Stateless,
        _ when !info.Enabled => AlertRuleSuitability.Disabled,
        _ => AlertRuleSuitability.Suitable,
    };

    /// <summary>"microsoft.insights/metricalerts" from ".../providers/Microsoft.Insights/metricAlerts/name".</summary>
    public static string TypeOf(string ruleId)
    {
        var segments = ruleId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var providers = Array.FindLastIndex(segments, s => s.Equals("providers", StringComparison.OrdinalIgnoreCase));
        return providers >= 0 && providers + 2 < segments.Length
            ? $"{segments[providers + 1]}/{segments[providers + 2]}".ToLowerInvariant()
            : string.Empty;
    }
}

/// <summary>
/// Whether the breaker can carry out what it decided. Every reason has a stable code. A circuit with
/// any reason is degraded; it is unhealthy only when its decision is not enforced: the switch that
/// should stop the work has failed repeatedly.
/// </summary>
public static class CircuitHealthRules
{
    public const int PollerStale = 101;
    public const int RuleNotFound = 102;
    public const int RuleStateless = 103;
    public const int RuleUnsupportedType = 104;
    public const int SwitchNotApplied = 201;

    /// <summary>A poller that has not succeeded for this many intervals is stale.</summary>
    public const int StaleAfterIntervals = 5;

    /// <summary>A switch that failed this often in a row means the decision is not enforced.</summary>
    public const int UnhealthyAfterSwitchFailures = 3;

    public static CircuitHealth Evaluate(CircuitData data, DateTimeOffset now)
    {
        var reasons = new List<HealthReason>();
        var unhealthy = false;
        var definition = data.RequiredDefinition;

        if (definition.AlertRuleIds.Count > 0 && data.Alerts is { } alerts)
        {
            var lastSuccess = alerts.LastSucceededAt ?? alerts.Since;
            if (alerts.Mode != AlertPollingMode.Off && now - lastSuccess > alerts.Interval * StaleAfterIntervals)
            {
                reasons.Add(new HealthReason(PollerStale,
                    $"No successful alert poll since {lastSuccess:O}{(alerts.LastError is null ? "" : $": {alerts.LastError}")}."));
            }

            AddRuleReasons(reasons, alerts.Rules, AlertRuleSuitability.NotFound, RuleNotFound,
                "Alert rule not found, or the breaker may not read it");
            AddRuleReasons(reasons, alerts.Rules, AlertRuleSuitability.Stateless, RuleStateless,
                "Alert rule does not resolve automatically and would hold the circuit open");
            AddRuleReasons(reasons, alerts.Rules, AlertRuleSuitability.UnsupportedType, RuleUnsupportedType,
                "Alert rule type is not supported; use a log search or metric alert rule");
        }

        if (data.Switch is { InSync: false, FailedAttempts: > 0 } sw)
        {
            reasons.Add(new HealthReason(SwitchNotApplied,
                $"Switch {sw.Target} should be {sw.Desired} but is {(sw.Applied?.ToString() ?? "unknown")} after {sw.FailedAttempts} failed attempt(s): {sw.LastError}"));
            unhealthy = sw.FailedAttempts >= UnhealthyAfterSwitchFailures;
        }

        var status = unhealthy ? CircuitHealthStatus.Unhealthy
            : reasons.Count > 0 ? CircuitHealthStatus.Degraded
            : CircuitHealthStatus.Healthy;
        return status == CircuitHealthStatus.Healthy ? CircuitHealth.Healthy : new CircuitHealth(status, reasons);
    }

    private static void AddRuleReasons(List<HealthReason> reasons, IEnumerable<AlertRuleStatus> rules, AlertRuleSuitability suitability, int code, string message)
    {
        foreach (var rule in rules.Where(r => r.Suitability == suitability))
        {
            reasons.Add(new HealthReason(code, $"{message}: {rule.RuleId}."));
        }
    }
}
