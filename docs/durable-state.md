# Durable state

Everything the breaker decides on is stored in the circuit's Durable Entity — not in the memory of a
process. A restart, a deployment, a scale-out or a crash loses nothing.

## What is stored

[CircuitData.cs](../src/CircuitBreaker.Core/CircuitData.cs) is the entity's state, persisted as JSON:

| Field | Purpose |
|---|---|
| `Definition` | threshold, window, timeouts, probe limits, switch — as registered by the callers |
| `State`, `Generation`, `StateChangedAt` | where the circuit is, how many transitions it made, since when |
| `Window` | the failure window: up to 11 buckets of counts |
| `RecoveryAt`, `ConsecutiveReopens` | when the next probe is allowed; how often it reopened in a row (drives the backoff) |
| `Probes`, `ProbeSuccesses` | active probe leases with their expiry; successes so far while half-open |
| `LastFailure`, `LastOutcomeAt`, `LastSeenAt` | for operators and for staleness |
| `Switch` | desired and applied position, last error, retry schedule |
| `History` | the 20 most recent transitions: from, to, trigger, reason, actor, time |
| `NextStalenessCheckAt` | the single pending staleness check |

A typical circuit's state is a few kilobytes. It is stored in the Durable Functions task hub in the
Function's storage account.

## Rules for durable state

1. **Plain data only.** Public properties with setters, lists, records. No services, no delegates,
   no `TimeSpan` arithmetic hidden in getters. Computed properties are marked `[JsonIgnore]`.
2. **Bounded.** The window has at most 11 buckets, history 20 entries, probe leases at most
   `HalfOpenMaxProbes`. State that grows with traffic eventually makes every operation slow.
3. **Timestamps, not timers in memory.** "Open until" is `RecoveryAt` in the state plus a durable
   signal scheduled for that moment; nothing waits in a process.
4. **Versioned by shape, not by number.** New fields get defaults, so old state still loads. For a
   breaking change, rename the task hub (`TaskHubName`) and let callers register afresh: a breaker's
   state is cheap to rebuild.

The test `The_state_is_stored_as_readable_json` in
[CircuitEntityTests.cs](../tests/CircuitBreaker.Function.Tests/CircuitEntityTests.cs) checks the
stored form; every entity test round-trips the state through JSON, so a property that does not
serialize fails at once.

## Durable timers

An entity can signal itself at a future time. That is how the breaker wakes up without traffic:

| Signal | Scheduled when | Carries |
|---|---|---|
| `RecoveryTimeoutElapsed` | the circuit opens | the generation it was scheduled in |
| `ProbeLeaseExpired` | a probe permit is granted | probe id and generation |
| `ReconcileSwitch` | flipping a switch failed | the due time |
| `CheckStaleness` | the circuit is registered, and after each check | the due time |

Signals can be delivered more than once, and can outlive the situation they were scheduled for. Each
carries a token — a generation or a due time — and the entity ignores a signal whose token no longer
matches. See [concurrency.md](concurrency.md#stale-and-duplicate-signals).

## Lifecycle

- **Created** when a caller registers the circuit. Other operations on an unknown circuit do not
  create it: they log a warning and leave nothing behind, so a typo in a key does not create a
  phantom circuit.
- **Kept alive** by registrations (on start-up and hourly) and outcome reports.
- **Deleted** when nothing has registered or reported for `StaleAfter` (default 7 days), or by an
  operator. A caller that still uses it registers it again and it starts closed.

## Where the state lives

Durable Functions stores entity state in the task hub of its storage provider — Azure Storage by
default (tables, queues and blobs in the Function's storage account). The
[Durable Task Scheduler](https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler)
and MSSQL providers work too, without code changes. Back up nothing: the state is operational, and a
lost task hub only means circuits start closed and callers register them again.
