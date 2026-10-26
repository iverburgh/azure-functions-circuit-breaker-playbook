using System.Text.Json.Serialization;

namespace CircuitBreaker.Contracts;

/// <summary>
/// The states of a circuit. <see cref="Closed"/>, <see cref="Open"/> and <see cref="HalfOpen"/> are
/// driven automatically by outcomes and time; <see cref="Isolated"/> and <see cref="Disabled"/> are
/// manual overrides that only an operator enters and leaves.
/// </summary>
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
