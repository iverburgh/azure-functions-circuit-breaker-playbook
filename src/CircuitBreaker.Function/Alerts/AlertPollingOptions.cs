using CircuitBreaker.Contracts;
using CircuitBreaker.Core;

namespace CircuitBreaker.Function.Alerts;

/// <summary>Configuration section <c>AlertPolling</c>. See docs/alert-driven-opening.md.</summary>
public sealed class AlertPollingOptions
{
    /// <summary>
    /// <see cref="AlertPollingMode.Off"/> by default: release the new states to every reader first,
    /// then try <see cref="AlertPollingMode.Shadow"/>, then switch to <see cref="AlertPollingMode.Active"/>.
    /// </summary>
    public AlertPollingMode Mode { get; set; } = AlertPollingMode.Off;

    /// <summary>
    /// How often each circuit with linked rules queries Resource Graph. Resource Graph throttles per
    /// identity (about 15 queries per 5 seconds), so many circuits need a longer interval.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A query that takes longer counts as a failed poll; it never blocks the entity.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Random spread of the interval (0.2 = ±20%), so circuits do not poll in step.</summary>
    public double Jitter { get; set; } = 0.2;

    public AlertPollSettings ToSettings() =>
        new(Mode, Interval < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : Interval, Jitter);
}
