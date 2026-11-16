using CircuitBreaker.Core;

namespace CircuitBreaker.Function.Alerts;

/// <summary>Reads linked alert rules and their firing alerts. The only part of alert polling that talks to Azure.</summary>
public interface IAlertRuleClient
{
    /// <summary>
    /// Returns the rules that exist and that the breaker may read. A rule that is missing from the
    /// result is either deleted or not readable: Resource Graph does not tell the two apart.
    /// </summary>
    /// <exception cref="AlertQueryException">The query failed; the caller records it and changes no state.</exception>
    Task<IReadOnlyList<AlertRuleInfo>> GetRulesAsync(IReadOnlyCollection<string> ruleIds, CancellationToken cancellationToken);
}

public enum AlertQueryFailure
{
    Timeout,
    Throttled,
    Unauthorized,
    ServerError,
    Network,
    InvalidResponse,
}

/// <summary>A failed alert query, with a kind that is safe to show and to group on.</summary>
public sealed class AlertQueryException(AlertQueryFailure kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AlertQueryFailure Kind { get; } = kind;
}
