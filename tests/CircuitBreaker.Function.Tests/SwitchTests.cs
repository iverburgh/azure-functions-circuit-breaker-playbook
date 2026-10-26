using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using CircuitBreaker.Contracts;
using CircuitBreaker.Function.Switches;

namespace CircuitBreaker.Function.Tests;

public sealed class SwitchTests
{
    [Test]
    public async Task The_feature_flag_is_disabled_while_open_and_enabled_while_closed()
    {
        var client = new FakeConfigurationClient(new FeatureFlagConfigurationSetting("payments", isEnabled: true));
        var flag = new FeatureFlagSwitch(client, "payments", label: "circuit-breaker");

        await flag.SetAsync(SwitchPosition.Open, CancellationToken.None);
        Assert.That(client.Flag.IsEnabled, Is.False);
        Assert.That(client.RequestedLabel, Is.EqualTo("circuit-breaker"));

        await flag.SetAsync(SwitchPosition.Closed, CancellationToken.None);
        Assert.That(client.Flag.IsEnabled, Is.True);
    }

    [Test]
    public async Task Setting_the_flag_to_its_current_value_writes_nothing()
    {
        var client = new FakeConfigurationClient(new FeatureFlagConfigurationSetting("payments", isEnabled: true));

        await new FeatureFlagSwitch(client, "payments", null).SetAsync(SwitchPosition.Closed, CancellationToken.None);

        Assert.That(client.Writes, Is.Zero);
    }

    [Test]
    public async Task A_concurrent_edit_of_the_flag_is_retried()
    {
        var client = new FakeConfigurationClient(new FeatureFlagConfigurationSetting("payments", isEnabled: true)) { ConflictsLeft = 1 };

        await new FeatureFlagSwitch(client, "payments", null).SetAsync(SwitchPosition.Open, CancellationToken.None);

        Assert.That(client.Flag.IsEnabled, Is.False);
        Assert.That(client.Writes, Is.EqualTo(2));
    }

    [Test]
    public async Task The_subscription_stops_receiving_while_open()
    {
        var client = new FakeServiceBusAdministrationClient();
        var subscription = new ServiceBusSubscriptionSwitch(client, "orders", "payments");

        await subscription.SetAsync(SwitchPosition.Open, CancellationToken.None);
        Assert.That(client.Status, Is.EqualTo(EntityStatus.ReceiveDisabled));

        await subscription.SetAsync(SwitchPosition.Open, CancellationToken.None);
        Assert.That(client.Updates, Is.EqualTo(1), "Already disabled: no second update.");

        await subscription.SetAsync(SwitchPosition.Closed, CancellationToken.None);
        Assert.That(client.Status, Is.EqualTo(EntityStatus.Active));
    }

    private sealed class FakeConfigurationClient(FeatureFlagConfigurationSetting flag) : ConfigurationClient
    {
        public FeatureFlagConfigurationSetting Flag { get; private set; } = flag;

        public string? RequestedLabel { get; private set; }

        public int Writes { get; private set; }

        public int ConflictsLeft { get; set; }

        public override Task<Response<ConfigurationSetting>> GetConfigurationSettingAsync(string key, string? label = null, CancellationToken cancellationToken = default)
        {
            Assert.That(key, Is.EqualTo(FeatureFlagConfigurationSetting.KeyPrefix + "payments"));
            RequestedLabel = label;
            var copy = new FeatureFlagConfigurationSetting(Flag.FeatureId, Flag.IsEnabled);
            return Task.FromResult(Response.FromValue<ConfigurationSetting>(copy, new FakeResponse(200)));
        }

        public override Task<Response<ConfigurationSetting>> SetConfigurationSettingAsync(ConfigurationSetting setting, bool onlyIfUnchanged = false, CancellationToken cancellationToken = default)
        {
            Writes++;
            Assert.That(onlyIfUnchanged, Is.True, "Writes must be conditional, so concurrent edits are detected.");
            if (ConflictsLeft-- > 0)
            {
                throw new RequestFailedException(412, "Precondition failed");
            }

            Flag = (FeatureFlagConfigurationSetting)setting;
            return Task.FromResult(Response.FromValue(setting, new FakeResponse(200)));
        }
    }

    private sealed class FakeServiceBusAdministrationClient : ServiceBusAdministrationClient
    {
        public EntityStatus Status { get; private set; } = EntityStatus.Active;

        public int Updates { get; private set; }

        public override Task<Response<SubscriptionProperties>> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            var properties = ServiceBusModelFactory.SubscriptionProperties(
                topicName, subscriptionName, lockDuration: TimeSpan.FromSeconds(30), defaultMessageTimeToLive: TimeSpan.FromDays(1),
                autoDeleteOnIdle: TimeSpan.MaxValue, maxDeliveryCount: 10, status: Status, userMetadata: string.Empty);
            return Task.FromResult(Response.FromValue(properties, new FakeResponse(200)));
        }

        public override Task<Response<SubscriptionProperties>> UpdateSubscriptionAsync(SubscriptionProperties subscription, CancellationToken cancellationToken = default)
        {
            Updates++;
            Status = subscription.Status;
            return Task.FromResult(Response.FromValue(subscription, new FakeResponse(200)));
        }
    }

    private sealed class FakeResponse(int status) : Response
    {
        public override int Status => status;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
