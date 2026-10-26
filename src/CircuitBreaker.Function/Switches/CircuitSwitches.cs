using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Azure.Messaging.ServiceBus.Administration;
using CircuitBreaker.Contracts;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Function.Switches;

/// <summary>
/// Something outside the breaker that stops or resumes work: a feature flag that a scheduled job
/// checks, or a queue subscription that a consumer reads. Setting it must be idempotent: the entity
/// may run an operation again after a crash.
/// </summary>
public interface ICircuitSwitch
{
    Task SetAsync(SwitchPosition position, CancellationToken cancellationToken);
}

public interface ICircuitSwitchProvider
{
    ICircuitSwitch Get(SwitchDefinition definition);
}

public sealed class SwitchOptions
{
    /// <summary>App Configuration stores by name, for example <c>"main": "https://my-store.azconfig.io"</c>.</summary>
    public Dictionary<string, Uri> AppConfigurationStores { get; set; } = [];

    /// <summary>Label of the feature flags the breaker manages. Keep them apart from flags that people edit.</summary>
    public string? FeatureFlagLabel { get; set; }

    /// <summary>Fully qualified Service Bus namespace, for example <c>my-namespace.servicebus.windows.net</c>.</summary>
    public string? ServiceBusNamespace { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

internal sealed class CircuitSwitchProvider(IOptions<SwitchOptions> options, TokenCredential credential) : ICircuitSwitchProvider
{
    private readonly ConcurrentDictionary<string, ConfigurationClient> _configurationClients = new();
    private ServiceBusAdministrationClient? _serviceBusClient;

    public ICircuitSwitch Get(SwitchDefinition definition) => definition.Kind switch
    {
        SwitchKind.FeatureFlag => new FeatureFlagSwitch(ConfigurationClientFor(definition.Store!), definition.FeatureFlag!, options.Value.FeatureFlagLabel),
        SwitchKind.ServiceBusSubscription => new ServiceBusSubscriptionSwitch(ServiceBusClient(), definition.Topic!, definition.Subscription!),
        _ => throw new NotSupportedException($"Switch kind {definition.Kind} is not supported."),
    };

    private ConfigurationClient ConfigurationClientFor(string store) => _configurationClients.GetOrAdd(store, name =>
        options.Value.AppConfigurationStores.TryGetValue(name, out var endpoint)
            ? new ConfigurationClient(endpoint, credential)
            : throw new InvalidOperationException($"App Configuration store '{name}' is not configured under Switches:AppConfigurationStores."));

    private ServiceBusAdministrationClient ServiceBusClient() => _serviceBusClient ??= new ServiceBusAdministrationClient(
        options.Value.ServiceBusNamespace ?? throw new InvalidOperationException("Switches:ServiceBusNamespace is not configured."),
        credential);
}

/// <summary>Enabled while the circuit lets work through, disabled while it is open.</summary>
internal sealed class FeatureFlagSwitch(ConfigurationClient client, string featureFlag, string? label) : ICircuitSwitch
{
    public async Task SetAsync(SwitchPosition position, CancellationToken cancellationToken)
    {
        var enabled = position == SwitchPosition.Closed;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await client.GetConfigurationSettingAsync(FeatureFlagConfigurationSetting.KeyPrefix + featureFlag, label, cancellationToken);
                if (response.Value is not FeatureFlagConfigurationSetting setting)
                {
                    throw new InvalidOperationException($"'{featureFlag}' is not a feature flag.");
                }

                if (setting.IsEnabled == enabled)
                {
                    return;
                }

                setting.IsEnabled = enabled;
                await client.SetConfigurationSettingAsync(setting, onlyIfUnchanged: true, cancellationToken);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status == 412 && attempt < 3)
            {
                // Someone changed the flag between our read and write: read it again.
            }
        }
    }
}

/// <summary>Receive-disabled while the circuit is open: messages wait in the subscription instead of failing one by one.</summary>
internal sealed class ServiceBusSubscriptionSwitch(ServiceBusAdministrationClient client, string topic, string subscription) : ICircuitSwitch
{
    public async Task SetAsync(SwitchPosition position, CancellationToken cancellationToken)
    {
        var status = position == SwitchPosition.Open ? EntityStatus.ReceiveDisabled : EntityStatus.Active;
        SubscriptionProperties properties = await client.GetSubscriptionAsync(topic, subscription, cancellationToken);
        if (properties.Status == status)
        {
            return;
        }

        properties.Status = status;
        await client.UpdateSubscriptionAsync(properties, cancellationToken);
    }
}
