# Switches

Some work does not go through a call you can guard: a consumer that reads a queue, a job that runs
every five minutes. Refusing such work item by item is wasteful — the consumer pulls every message,
fails it, and retries it until it is dead-lettered. A **switch** stops the work at the source.

| Switch | Open circuit | Closed circuit | For |
|---|---|---|---|
| Service Bus subscription | `ReceiveDisabled`: messages wait in the subscription | `Active` | queue and topic consumers |
| App Configuration feature flag | disabled | enabled | scheduled jobs, background loops, UI features |

The breaker flips the switch whenever the circuit's state changes. Only `Open` and `Isolated` open the
switch; `Closed`, `HalfOpen` and `Disabled` close it.

## Configure a switch

On the circuit:

```csharp
new CircuitDefinition
{
    Key = "invoice-export",
    DisplayName = "Invoice export",
    Switch = new SwitchDefinition { Kind = SwitchKind.ServiceBusSubscription, Topic = "invoices", Subscription = "export" },
}
```

```csharp
Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = "invoice-export-enabled" }
```

On the Function, where to find them (app settings, or `local.settings.json`):

| Setting | Example |
|---|---|
| `Switches__ServiceBusNamespace` | `my-namespace.servicebus.windows.net` |
| `Switches__AppConfigurationStores__main` | `https://my-store.azconfig.io` |
| `Switches__FeatureFlagLabel` | `circuit-breaker` (optional) |

The Function's managed identity needs **Azure Service Bus Data Owner** on the namespace or
**App Configuration Data Owner** on the store. The [Bicep template](../infra/main.bicep) assigns them
when you pass the names.

## The consumer side

A **queue consumer** needs no code: a receive-disabled subscription simply delivers nothing.
Messages stay in the subscription until it is active again — make sure their time-to-live is longer
than your longest expected outage.

A **scheduled job** checks the flag before it starts:

```csharp
[Function(nameof(ExportInvoices))]
public async Task ExportInvoices([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
{
    if (!await featureManager.IsEnabledAsync("invoice-export-enabled", cancellationToken))
    {
        logger.LogInformation("Invoice export skipped: its circuit is open.");
        return;
    }

    // ... and report the outcomes of the calls it makes, so the circuit can close again.
}
```

Feature flags reach applications through their configuration refresh, typically every 30 seconds:
add that to the detection latency.

## Reporting outcomes from switch-driven work

The switch only acts on what the circuit decides, and the circuit decides on outcomes. Consumers must
still report them — through the client library, or by calling the breaker's API:

```csharp
await breaker.ExecuteAsync(token => exporter.ExportAsync(message, token), cancellationToken);
```

## Half-open and switches

A switch cannot hand out probe permits: a subscription is either receiving or not. While half-open
the switch is closed, work flows again, and the first results decide. To keep that gentle:

- **limit consumer concurrency** (`MaxConcurrentCalls`, `MaxConcurrentSessions`), so recovery starts
  with a trickle;
- **or let the consumer use the client per message.** In half-open, messages without a probe permit
  get a `BrokenCircuitException`; abandon them with a delay and the probe limit applies to the
  consumer too.

## When a switch cannot be flipped

The circuit's decision stands; only the switch lags. The entity records the error, shows it in the
console ("switch failing"), logs event 1006, and retries with backoff — 15 s, 30 s, 1 min, up to
10 min — until it succeeds or the circuit no longer needs it. It never throws: throwing would roll
back the state change and lose the decision.

Common causes: a missing role assignment (403), a misspelled flag or subscription (404), a store or
namespace not configured. Fix it, and the next retry succeeds on its own.

## Rules

- **The breaker owns the switch.** Do not flip a breaker-managed flag or subscription by hand: the
  breaker will flip it back at the next transition. Isolate or disable the circuit instead. A
  dedicated label (`Switches__FeatureFlagLabel`) keeps breaker-managed flags apart from flags that
  people edit.
- **Create the flag or subscription with your infrastructure.** The breaker sets existing switches;
  it never creates them.
- **Switches must be idempotent.** Both implementations read the current value and write only when it
  differs — the entity may run an operation again after a crash. The feature flag switch writes
  conditionally and re-reads on a concurrent edit.
