# Operations

What to watch, and what to do when something happens. For the log events, metrics, queries and
alert rules themselves, see [observability-and-testing.md](observability-and-testing.md).

## Daily view

The [management console](management-ui.md) answers the first questions: which circuits are not
closed, since when, why (the last failure and the history), and whether their switches followed.

| You see | It means | Usually |
|---|---|---|
| `Open`, `consecutiveReopens` 0 | a dependency started failing recently | wait; the breaker probes on its own |
| `Open`, `consecutiveReopens` rising | probes keep failing; recovery timeouts are doubling | look at the dependency, not at the breaker |
| `HalfOpen` for longer than a few minutes | no caller is asking for a probe | normal when traffic is low; the next call probes |
| `Isolated` or `Disabled` | an operator overrode it | check the history for who and why; do not leave it forgotten |
| `OpenedByAlert` | a linked alert rule fires; **Tripped by** names it | fix the cause; see [a circuit is opened by an alert](#a-circuit-is-opened-by-an-alert) |
| `OpenedAfterAlert` | the alert resolved; the circuit waits for a person | check the cause is really gone, then **Reset** |
| health callout (Degraded or Unhealthy) | the breaker cannot fully carry out what it decides | read the reason code, see [health](observability-and-testing.md#health) |
| switch with an error | the breaker decided, but could not flip the switch | see [a switch is out of sync](#a-switch-is-out-of-sync) |
| failures in the window close to the threshold | the dependency is degraded | check whether the threshold fits the traffic |

## Procedures

### A dependency is down and you know it will stay down

Isolate its circuit with a reason ("vendor incident, ticket reference"). Callers refuse at once,
switches stop queue consumers and scheduled jobs, and no probe hits the dependency until you decide.
When the vendor confirms the fix, **Reset**.

### The dependency is fixed and you do not want to wait

Reset the circuit. It closes with a clean window and no backoff. If the fix was not real, the circuit
opens again as soon as the threshold is reached.

### A circuit is opened by an alert

The circuit stays open while the alert fires, and **Reset** is not offered. Follow the runbook in
[alert-driven-opening.md](alert-driven-opening.md#runbook-a-circuit-is-opened-by-an-alert): read
**Tripped by**, fix the cause, repair data, close the alert, **Check alerts now**, then **Reset**. To
let work flow while the alert still fires, **Disable** the circuit.

### Planned pause of intake

For maintenance, a migration or a restore, you want a system to stop taking in new work and let the
work already inside drain — not to stop everything halfway.

1. **Find the intake circuits**: the ones whose switch stops work at the entry point (the first
   consumer of incoming messages, the job that fetches new items). Read the keys from
   `GET /api/circuits` or the console; never build keys by hand from a naming convention, a typo
   silently isolates nothing.
2. **Isolate them** with one reason ("planned maintenance, change reference"), through the console or
   `POST /api/circuits/{key}/actions/isolate`. New work waits in its subscription.
3. **Wait until downstream work has dried up**: queues behind the intake empty, no more outcomes for
   the downstream circuits.
4. **Handle what has no circuit.** Timers, side paths and manual triggers outside any circuit keep
   running; stop them separately (disable the function, a feature flag) and write them in your
   runbook.
5. Do the maintenance.
6. **Reset** the intake circuits (and restart what you stopped by hand). Work that waited resumes.

Script the isolate and reset steps against the API with the Operator role, so the pause is the same
every time and recorded with actor and reason in each circuit's history.

### The breaker opens when it should not

1. Look at the last failure and its category. If it is a functional error, the caller's
   [classification](failure-classification.md) is wrong — fix it in the caller.
2. If the failures are real but harmless at that volume, raise the threshold or widen the window in
   the circuit's definition, in the caller's code, and deploy. The new definition is registered on
   start-up. See [failure-threshold.md](failure-threshold.md#changing-the-threshold).
3. Until then, **Disable** the circuit: calls flow, outcomes are still recorded, it never opens.

### The breaker itself is unavailable

Callers keep using the last state they knew, and let calls through when they knew nothing
([fail-fast.md](fail-fast.md#when-the-breaker-itself-is-unreachable)). A circuit that was open when
the breaker went away stays refused in its callers until the breaker answers again, because a probe
permit cannot be obtained. If that is worse than the risk, restart those callers: without a known
state they fall back to `WhenBreakerUnavailable`, which allows calls by default.

### A switch is out of sync

The entity retries on its own, with a delay that doubles from 15 seconds to at most 10 minutes. The
console shows the last error. Common causes:

| Error | Fix |
|---|---|
| 403 / authorization failed | grant the Function's managed identity the role, see [security-and-deployment.md](security-and-deployment.md#permissions-for-switches) |
| 404 / not found | the feature flag (with the configured label) or the subscription does not exist; create it in your infrastructure code |
| store not configured | add `Switches:AppConfigurationStores:<name>` to the Function's settings |

The next retry, or the next state change, picks the fix up.

### A circuit is no longer used

Do nothing: a circuit without registrations or outcomes for `StaleAfter` (default 7 days) deletes
itself. Or delete it in the console; if a caller still uses it, it registers it again and starts
closed.

### Starting with empty state

Changing `taskHubName` gives the breaker a new, empty task hub: every circuit starts closed when its
callers register it again, and pending timers of the old hub are abandoned. Use it after an
incompatible change to the entity's state; delete the old hub's tables, queues and containers
afterwards.

## Introducing a new circuit safely

1. Deploy the caller with the circuit defined. It registers on start-up and starts closed.
2. **Disable** it in the console right away. Calls flow and outcomes are recorded, but it never opens:
   a dry run.
3. Watch failures in the window for a while, through normal peaks. If they would have crossed the
   threshold without a real incident, adjust the definition.
4. **Reset** to make it live.

## Capacity

One entity per circuit processes its operations one at a time. Clients batch outcomes per second
per instance, so a circuit handles roughly as many operations per second as there are caller
instances, independent of the call volume. Probe requests only happen while half-open. If a circuit
ever falls behind — operations waiting in the control queues — increase `ReportInterval` in the
callers before anything else. See [concurrency.md](concurrency.md#throughput-batch-dont-stream).
