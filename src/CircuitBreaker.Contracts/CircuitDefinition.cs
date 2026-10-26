using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CircuitBreaker.Contracts;

/// <summary>
/// Everything the breaker needs to know about one circuit. Callers register it on start-up, so the
/// configuration lives with the code that depends on the circuit and is versioned with it.
/// </summary>
public sealed partial record CircuitDefinition
{
    /// <summary>Stable identifier: lower case letters, digits and hyphens, for example <c>payments-api</c>.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>How many counted failures within <see cref="FailureWindow"/> open the circuit.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>The sliding window in which failures are counted.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The failure categories that count towards <see cref="FailureThreshold"/>. Functional errors do not by default.</summary>
    public IReadOnlyList<FailureCategory> CountedCategories { get; init; } =
        [FailureCategory.Timeout, FailureCategory.Exception, FailureCategory.ServerError, FailureCategory.Throttled];

    /// <summary>How long the circuit stays open before the first probe is allowed.</summary>
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the recovery timeout, which doubles every time a probe fails.</summary>
    public TimeSpan MaxRecoveryTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Random spread applied to the recovery timeout, as a fraction (0.1 = ±10%).</summary>
    public double RecoveryJitter { get; init; } = 0.1;

    /// <summary>How many probe calls may be in flight at the same time while half-open.</summary>
    public int HalfOpenMaxProbes { get; init; } = 1;

    /// <summary>How many successful probes close the circuit again.</summary>
    public int HalfOpenSuccessThreshold { get; init; } = 2;

    /// <summary>How long a probe may take before it is treated as failed. Keep it above the caller's attempt timeout.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A circuit that has not been registered or reported on for this long is deleted.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Optional: something to flip when the circuit opens or closes, for work that does not pass through the client.</summary>
    public SwitchDefinition? Switch { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 100 || !KeyPattern().IsMatch(Key))
        {
            errors.Add("Key must be 1-100 characters: lower case letters, digits and hyphens.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("DisplayName is required.");
        }

        if (FailureThreshold < 1)
        {
            errors.Add("FailureThreshold must be at least 1.");
        }

        if (FailureWindow < TimeSpan.FromSeconds(1) || FailureWindow > TimeSpan.FromHours(1))
        {
            errors.Add("FailureWindow must be between 1 second and 1 hour.");
        }

        if (CountedCategories.Count == 0)
        {
            errors.Add("CountedCategories must contain at least one category.");
        }

        if (RecoveryTimeout < TimeSpan.FromSeconds(1) || MaxRecoveryTimeout < RecoveryTimeout)
        {
            errors.Add("RecoveryTimeout must be at least 1 second and not exceed MaxRecoveryTimeout.");
        }

        if (RecoveryJitter is < 0 or > 0.5)
        {
            errors.Add("RecoveryJitter must be between 0 and 0.5.");
        }

        if (HalfOpenMaxProbes < 1 || HalfOpenSuccessThreshold < 1)
        {
            errors.Add("HalfOpenMaxProbes and HalfOpenSuccessThreshold must be at least 1.");
        }

        if (ProbeTimeout < TimeSpan.FromSeconds(1))
        {
            errors.Add("ProbeTimeout must be at least 1 second.");
        }

        if (StaleAfter < TimeSpan.FromHours(1))
        {
            errors.Add("StaleAfter must be at least 1 hour.");
        }

        if (Switch is not null)
        {
            errors.AddRange(Switch.Validate());
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KeyPattern();
}

/// <summary>What to flip when the circuit changes position. See <see cref="SwitchKind"/>.</summary>
public sealed record SwitchDefinition
{
    public required SwitchKind Kind { get; init; }

    /// <summary>For <see cref="SwitchKind.FeatureFlag"/>: the name of the configured App Configuration store.</summary>
    public string? Store { get; init; }

    /// <summary>For <see cref="SwitchKind.FeatureFlag"/>: the feature flag name.</summary>
    public string? FeatureFlag { get; init; }

    /// <summary>For <see cref="SwitchKind.ServiceBusSubscription"/>: the topic.</summary>
    public string? Topic { get; init; }

    /// <summary>For <see cref="SwitchKind.ServiceBusSubscription"/>: the subscription.</summary>
    public string? Subscription { get; init; }

    [JsonIgnore]
    public string Target => Kind == SwitchKind.FeatureFlag ? $"{Store}/{FeatureFlag}" : $"{Topic}/{Subscription}";

    public IReadOnlyList<string> Validate() => Kind switch
    {
        SwitchKind.FeatureFlag when string.IsNullOrWhiteSpace(Store) || string.IsNullOrWhiteSpace(FeatureFlag) =>
            ["A feature flag switch needs Store and FeatureFlag."],
        SwitchKind.ServiceBusSubscription when string.IsNullOrWhiteSpace(Topic) || string.IsNullOrWhiteSpace(Subscription) =>
            ["A Service Bus switch needs Topic and Subscription."],
        _ => [],
    };
}
