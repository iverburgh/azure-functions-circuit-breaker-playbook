using CircuitBreaker.Contracts;

namespace CircuitBreaker.Client;

public enum BreakerUnavailableBehavior
{
    /// <summary>Let calls through when the breaker cannot be reached and no state is known. The breaker must not become the outage.</summary>
    Allow,

    /// <summary>Refuse calls when the breaker cannot be reached and no state is known.</summary>
    Reject,
}

public sealed class CircuitBreakerClientOptions
{
    /// <summary>Base address of the circuit breaker Function, for example <c>https://my-breaker.azurewebsites.net/</c>.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>How long a known circuit state is trusted before it is fetched again.</summary>
    public TimeSpan SnapshotTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often outcomes are sent, aggregated. Probe results are sent immediately.</summary>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Outcomes older than this are dropped if they could not be sent: late failures would distort the window.</summary>
    public TimeSpan MaxReportAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for calls to the breaker itself. Keep it short: these calls are on your request path.</summary>
    public TimeSpan BreakerCallTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public BreakerUnavailableBehavior WhenBreakerUnavailable { get; set; } = BreakerUnavailableBehavior.Allow;

    /// <summary>Outcomes waiting to be aggregated. When full, new outcomes are dropped (and counted) rather than blocking calls.</summary>
    public int ReportQueueCapacity { get; set; } = 10_000;

    public TimeSpan RegistrationRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Circuits are registered again periodically, so a circuit deleted as stale comes back.</summary>
    public TimeSpan RegistrationRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Identifies this instance in reports and probe leases.</summary>
    public string Source { get; set; } = Environment.MachineName;
}

/// <summary>Client-side settings of one circuit: its definition, and how calls through it are timed and judged.</summary>
public sealed class CircuitOptions
{
    public required CircuitDefinition Definition { get; init; }

    /// <summary>The timeout of one call. Keep it below <see cref="CircuitDefinition.ProbeTimeout"/>.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public IFailureClassifier Classifier { get; init; } = new DefaultFailureClassifier();
}

/// <summary>Thrown instead of calling the dependency, because the circuit does not allow it right now.</summary>
public sealed class BrokenCircuitException(string circuitKey, CircuitState? state, DateTimeOffset? retryAfter, string message)
    : Exception(message)
{
    public string CircuitKey { get; } = circuitKey;

    /// <summary>The state that refused the call, or <c>null</c> when the breaker could not be reached.</summary>
    public CircuitState? State { get; } = state;

    /// <summary>When a new attempt may be allowed, if known.</summary>
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}
