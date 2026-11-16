# Switches

Some work does not go through a call you can guard: a consumer that reads a queue, a job that runs
every five minutes. Refusing such work item by item is wasteful — the consumer pulls every message,
fails it, and retries it until it is dead-lettered. A **switch** stops the work at the source.

| Switch | Open circuit | Closed circuit | For |
|---|---|---|---|
| Service Bus subscription | `ReceiveDisabled`: messages wait in the subscription | `Active` | queue and topic consumers |
| App Configuration feature flag | disabled | enabled | scheduled jobs, background loops, UI features |

The breaker flips the switch whenever the circuit's state changes. `Open`, `OpenedByAlert`,
`OpenedAfterAlert` and `Isolated` open the switch; `Closed`, `HalfOpen` and `Disabled` close it.

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

## Per-key circuits: one partner at a time

Sometimes one dependency fails for a part of the traffic only: deliveries to partner 42 fail, the
other partners are fine. One circuit for "partner delivery" would stop everyone. Give each part its
own circuit, with the part in the key:

```csharp
foreach (var partner in partners)
{
    circuitBreakers.AddCircuit(new CircuitOptions
    {
        Definition = new CircuitDefinition
        {
            Key = $"partner-delivery-{partner.Id}",
            DisplayName = $"Delivery to {partner.Name}",
            Switch = new SwitchDefinition { Kind = SwitchKind.FeatureFlag, Store = "main", FeatureFlag = $"partner-delivery-{partner.Id}-enabled" },
        },
    });
}
```

The caller picks the circuit per item, with
`serviceProvider.GetRequiredKeyedService<ICircuitBreaker>($"partner-delivery-{message.PartnerId}")`.
Each circuit counts, recovers and is managed on its own, and the console shows exactly which partner
is stopped.
Keep the number of keys bounded and known up front: every key is an entity and a row in the console,
and a key derived from free input lets anyone create circuits.

### Alternative: one parameterized feature flag

When there are many parts, or they come and go, one flag per part is a lot of flags. A feature flag
with a **contextual feature filter** can hold the list instead: the filter's parameters name the parts
that are stopped, and the application asks per item:

```csharp
public sealed record PartnerContext(string PartnerId);

if (!await featureManager.IsEnabledAsync("partner-delivery", new PartnerContext(message.PartnerId)))
{
    // stopped for this partner: reschedule the message, see below
}
```

with a filter that implements `IContextualFeatureFilter<PartnerContext>`
([Microsoft docs](https://learn.microsoft.com/azure/azure-app-configuration/feature-management-dotnet-reference#contextual-feature-filters)).
Conventions that keep it safe:

- **A part that is not in the list flows.** An empty list, or a missing flag, stops nothing.
- **Opening adds the part** to the filter's parameters; **closing removes it** again.
- **Only at entry points.** Check the flag where an item enters your system (an HTTP intake, the
  first consumer), not halfway through a pipeline where stopping leaves half-done work.

This playbook's feature flag switch turns a whole flag on or off; a parameterized switch is an
extension you write yourself (add a `SwitchKind`, read the flag, change one parameter, write it back
conditionally — the same rules as below apply). Link no [alert rules](alert-driven-opening.md) to
parameterized or per-key circuits: an alert does not say which part is failing.

### Do not use one auto-forwarding subscription per part

A tempting design is one Service Bus subscription per partner, auto-forwarding to a shared queue, and
the subscription as the switch. It fails at half-open: the moment the subscription is active again,
**everything** that piled up is forwarded at once, and the recovering partner gets the whole backlog
instead of a probe. Use a parameterized flag or per-key circuit at the consumer, and reschedule
stopped messages (`ScheduledEnqueueTime`) or abandon them with a delay, so they come back gradually.

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
