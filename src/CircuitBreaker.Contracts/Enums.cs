using System.Text.Json.Serialization;

namespace CircuitBreaker.Contracts;

/// <summary>
/// The states of a circuit. <see cref="Closed"/>, <see cref="Open"/> and <see cref="HalfOpen"/> are
/// driven automatically by outcomes and time; <see cref="Isolated"/> and <see cref="Disabled"/> are
/// manual overrides that only an operator enters and leaves; <see cref="OpenedByAlert"/> and
/// <see cref="OpenedAfterAlert"/> are driven by Azure Monitor alert rules.
/// </summary>
/// <remarks>
/// Every enum in this assembly is append-only. Values travel as strings, and a reader that does not
/// know a value fails to read the whole message. Add new values at the end, deploy readers first, and
/// never rename or remove a value. A test guards this.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CircuitState>))]
public enum CircuitState
{
    /// <summary>Calls flow; failures are counted.</summary>
    Closed,

    /// <summary>Calls are refused without touching the dependency, until the recovery timeout elapses.</summary>
    Open,

    /// <summary>A limited number of probe calls test whether the dependency has recovered.</summary>
    HalfOpen,

    /// <summary>Forced open by an operator; never recovers automatically.</summary>
    Isolated,

    /// <summary>Forced closed by an operator; outcomes are recorded but never open the circuit.</summary>
    Disabled,

    /// <summary>Held open because a linked alert rule fires. Outcomes and timers are ignored; it cannot be reset while the alert fires.</summary>
    OpenedByAlert,

    /// <summary>The alert that opened the circuit has resolved. Stays open until a person resets it.</summary>
    OpenedAfterAlert,
}

/// <summary>Why a call failed. Each circuit decides which categories count towards opening it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FailureCategory>))]
public enum FailureCategory
{
    /// <summary>The call did not complete within its timeout.</summary>
    Timeout,

    /// <summary>A technical exception: connection refused, DNS, TLS, socket reset.</summary>
    Exception,

    /// <summary>The dependency answered, but with a server error (HTTP 5xx).</summary>
    ServerError,

    /// <summary>The dependency asked us to slow down (HTTP 429, or 503 with Retry-After).</summary>
    Throttled,

    /// <summary>A functional or business error (validation, not found, conflict). The dependency is healthy.</summary>
    Functional,
}

/// <summary>Manual actions an operator can take on a circuit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CircuitAction>))]
public enum CircuitAction
{
    Isolate,
    Disable,
    Reset,
    Delete,
}

[JsonConverter(typeof(JsonStringEnumConverter<SwitchKind>))]
public enum SwitchKind
{
    /// <summary>An Azure App Configuration feature flag: enabled while the circuit lets work through.</summary>
    FeatureFlag,

    /// <summary>An Azure Service Bus subscription: <c>ReceiveDisabled</c> while the circuit is open.</summary>
    ServiceBusSubscription,
}

[JsonConverter(typeof(JsonStringEnumConverter<SwitchPosition>))]
public enum SwitchPosition
{
    /// <summary>Work flows.</summary>
    Closed,

    /// <summary>Work is stopped.</summary>
    Open,
}

/// <summary>How the breaker acts on linked alert rules. Plain configuration, so it can be changed without a deployment of the clients.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertPollingMode>))]
public enum AlertPollingMode
{
    /// <summary>Alert rules are not queried. A circuit held open by an alert is handed to a person.</summary>
    Off,

    /// <summary>Alert rules are queried and the result is shown ("would open"), but no state changes.</summary>
    Shadow,

    /// <summary>A firing alert rule opens the circuit.</summary>
    Active,
}

/// <summary>Whether a linked alert rule can drive a circuit. Ordered by precedence: the first problem found wins.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertRuleSuitability>))]
public enum AlertRuleSuitability
{
    /// <summary>Found, stateful and enabled.</summary>
    Suitable,

    /// <summary>Not a log search or metric alert rule; other rule types never resolve on their own.</summary>
    UnsupportedType,

    /// <summary>Not found, or the breaker may not read it: Resource Graph does not tell these apart.</summary>
    NotFound,

    /// <summary>The rule does not resolve its alerts automatically, so it would hold the circuit open forever.</summary>
    Stateless,

    /// <summary>The rule is disabled. It counts as resolved; that is a valid way to release a circuit.</summary>
    Disabled,
}

[JsonConverter(typeof(JsonStringEnumConverter<CircuitHealthStatus>))]
public enum CircuitHealthStatus
{
    Healthy,

    /// <summary>Works, but something needs attention. See the reasons.</summary>
    Degraded,

    /// <summary>The breaker has decided something it cannot carry out.</summary>
    Unhealthy,
}
