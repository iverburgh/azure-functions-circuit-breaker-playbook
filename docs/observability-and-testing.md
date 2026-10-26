# Observability and testing

## What is logged

Every log event has a stable event id ([CircuitMetrics.cs](../src/CircuitBreaker.Function/Observability/CircuitMetrics.cs)),
so queries and alerts do not depend on message text.

| Event id | Level | When |
|---|---|---|
| 1001 | Information | a circuit was registered |
| 1002 | **Warning** | a circuit stopped calls: → `Open` or → `Isolated` |
| 1003 | Information | any other transition |
| 1004 | Information | a probe permit was requested (granted or not) |
| 1005 | Information | a switch was set |
| 1006 | **Error** | a switch could not be set; includes the retry time |
| 1007 | Debug | an operation changed nothing (stale timer, duplicate signal) |
| 1008 | Information | a circuit was deleted (stale, or by an operator) |
| 1009 | Warning | an operation for an unknown circuit (a caller that did not register) |
| 1010 | Information | a manual action, with actor, reason and result |

Transition events carry `CircuitKey`, `From`, `To`, `Trigger`, `Generation`, `Reason` and `Actor` as
structured properties. The client logs warnings when it cannot reach the breaker or report outcomes.

## Metrics

With `APPLICATIONINSIGHTS_CONNECTION_STRING` set, the Function exports these counters through
OpenTelemetry (they arrive in the `customMetrics` table):

| Metric | Tags | Use |
|---|---|---|
| `circuitbreaker.outcomes` | circuit, result, category | outcome volume and mix per dependency |
| `circuitbreaker.transitions` | circuit, from, to, trigger | how often circuits open, recover, reopen |
| `circuitbreaker.probe_requests` | circuit, state, result | probe pressure while recovering |
| `circuitbreaker.switch_updates` | circuit, result | switch health |

## Queries

Property names in `customDimensions` depend on how logs reach Application Insights; check one record
and adjust (`prop__CircuitKey` or `CircuitKey`). The queries below handle both.

Circuits that stopped calls in the last day:

```kusto
traces
| where timestamp > ago(1d)
| extend EventId = toint(coalesce(customDimensions.EventId, customDimensions.prop__EventId))
| where EventId == 1002
| extend Circuit = tostring(coalesce(customDimensions.CircuitKey, customDimensions.prop__CircuitKey)),
         Trigger = tostring(coalesce(customDimensions.Trigger, customDimensions.prop__Trigger))
| project timestamp, Circuit, Trigger, message
| order by timestamp desc
```

How long each outage lasted (from the first opening to closing again):

```kusto
traces
| where timestamp > ago(7d)
| extend EventId = toint(coalesce(customDimensions.EventId, customDimensions.prop__EventId)),
         Circuit = tostring(coalesce(customDimensions.CircuitKey, customDimensions.prop__CircuitKey)),
         From = tostring(coalesce(customDimensions.From, customDimensions.prop__From)),
         To = tostring(coalesce(customDimensions.To, customDimensions.prop__To))
| where EventId in (1002, 1003)
| where (From == "Closed" and To == "Open") or To == "Closed"
| order by Circuit asc, timestamp asc
| serialize
| extend OpenedAt = iff(To == "Closed" and prev(Circuit) == Circuit and prev(To) == "Open", prev(timestamp), datetime(null))
| where isnotnull(OpenedAt)
| project Circuit, OpenedAt, ClosedAt = timestamp, Duration = timestamp - OpenedAt
```

Transitions per hour, from the metrics:

```kusto
customMetrics
| where name == "circuitbreaker.transitions"
| extend Circuit = tostring(customDimensions.circuit), To = tostring(customDimensions.to)
| summarize Transitions = sum(valueSum) by bin(timestamp, 1h), Circuit, To
| render timechart
```

## Alerts worth having

| Alert | Signal | Why |
|---|---|---|
| A circuit opened | event 1002 | someone should know a dependency is down |
| A circuit keeps reopening | ≥ 3 × trigger `ProbeFailed` for one circuit in 30 min | a long outage, not a blip |
| A switch is failing | event 1006 | the circuit decided, but consumers are not stopped (or not restarted) |
| Callers cannot reach the breaker | client warning "Could not read circuit" | callers fall back to last known state |
| Unknown circuits | event 1009 | a caller is misconfigured or did not register |

The [management console](management-ui.md) shows the same in one screen: state, failures in the
window, recovery time, probes, failing switches and history.

## Testing

Each concern is tested where it is cheapest to test thoroughly.

| Concern | Where | How |
|---|---|---|
| Every transition, threshold, window, backoff, probe rule | [CircuitBreaker.Core.Tests](../tests/CircuitBreaker.Core.Tests) | pure functions with explicit time; a 5,000-step random walk checks invariants after every step |
| Documentation matches the state machine | `The_state_machine_diagram_in_the_documentation_matches_the_table` | parses the mermaid diagram in [state-machine.md](state-machine.md) |
| Entity behaviour, persistence, timers | [CircuitEntityTests.cs](../tests/CircuitBreaker.Function.Tests/CircuitEntityTests.cs) | the real entity under a simulated runtime |
| Crashes, re-execution, duplicate and stale signals, concurrency | [ReplayAndConcurrencyTests.cs](../tests/CircuitBreaker.Function.Tests/ReplayAndConcurrencyTests.cs) | the same harness, with crashes injected |
| Switches | [SwitchTests.cs](../tests/CircuitBreaker.Function.Tests/SwitchTests.cs) | the Azure SDK clients, faked |
| Authorization | [RequestAuthorizerTests.cs](../tests/CircuitBreaker.Function.Tests/RequestAuthorizerTests.cs) | principal headers |
| Fail-fast, classification, batching, retries | [CircuitBreaker.Client.Tests](../tests/CircuitBreaker.Client.Tests) | fake breaker API, `FakeTimeProvider`, a real `HttpClient` pipeline |
| Console | [ui/src/test](../ui/src/test) | Vitest and Testing Library |

### The entity harness

[EntityHarness.cs](../tests/CircuitBreaker.Function.Tests/EntityHarness.cs) plays the Durable runtime
closely enough to test what matters:

- **operations run one at a time**, even when tests call them from many threads;
- **state is stored as JSON** and read back before every operation, so anything that does not
  survive serialization fails;
- **signals are only sent when the operation commits**; a simulated crash discards the state change
  *and* the signals, exactly like the runtime;
- **scheduled signals are delivered when their time comes** as a `FakeTimeProvider` advances — and
  tests can deliver them twice.

With it, a test like this runs in milliseconds:

```csharp
await _harness.RegisterAsync();
await _harness.ReportAsync(Failures(3));                 // opens; schedules the recovery timer
await _harness.AdvanceAsync(TimeSpan.FromSeconds(30));   // the timer fires: half-open
var probe = await _harness.AcquireProbeAsync();
```

### Testing against the real runtime

The harness simulates the runtime; it does not replace trying the real one. Run the Function locally
with Azurite (see [getting-started.md](getting-started.md)) and drive a circuit through its states
with [CircuitBreaker.http](../src/CircuitBreaker.Function/CircuitBreaker.http) after changing the
entity, its state, or anything about serialization. That is how the eventual consistency of entity
queries was found — no unit test would have shown it.
