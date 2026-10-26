# Half-open probes

When the recovery timeout elapses, the circuit moves to `HalfOpen`. It does not open the floodgates:
it hands out a few **probe permits**, and only callers holding one may call the dependency. The
probes' results decide whether the circuit closes or opens again.

```csharp
new CircuitDefinition
{
    // ...
    HalfOpenMaxProbes = 1,                    // at most one probe in flight
    HalfOpenSuccessThreshold = 2,             // two successful probes close the circuit
    ProbeTimeout = TimeSpan.FromSeconds(30),  // a permit expires after this
}
```

## Why limit probes

Without a limit, half-open means "everyone may call again": every instance of every caller sends its
backlog at once to a dependency that may have recovered a minute ago and is still cold. That is how a
recovery turns into a second outage. With a limit, the dependency sees one or a few calls, and full
traffic only returns after they succeeded.

## How permits work

[CircuitEngine.TryAcquireProbe](../src/CircuitBreaker.Core/CircuitEngine.cs):

| State | Answer |
|---|---|
| `Closed`, `Disabled` | granted, no probe id: a normal call |
| `Open` | denied, with `RetryAfter = RecoveryAt` |
| `Isolated` | denied |
| `HalfOpen`, fewer than `HalfOpenMaxProbes` leases | granted, with a probe id and an expiry |
| `HalfOpen`, all slots taken | denied, with `RetryAfter` = the earliest lease expiry |

Because the entity runs one operation at a time, concurrent requests can never get more permits than
there are slots: 50 simultaneous requests for 3 slots get exactly 3 permits (test
`Concurrent_probe_requests_never_exceed_the_limit`).

## What a probe result means

The caller reports its probe's result with the probe id. The entity decides:

| Probe result | Meaning | Effect |
|---|---|---|
| success | the dependency works | slot freed; after `HalfOpenSuccessThreshold` successes → `Closed` |
| failure in a counted category (timeout, server error...) | still down | → `Open`, with a doubled recovery timeout |
| failure in an ignored category (functional) | the dependency answered — it is up | counts as a success |
| no category: the caller gave up, or never called | no information | slot freed, no verdict |
| lease expired without a result | the caller crashed or never got the permit | slot freed, no verdict |
| a result for an unknown probe id | from an earlier half-open period, or already expired | ignored |

Two choices here are deliberate:

- **An expired lease is not a failure.** The caller enforces its own attempt timeout and reports a
  timeout as a failure. A lease only expires when the permit got lost — a timeout on the way back, a
  crashed instance — and that says nothing about the dependency. Treating it as a failure would
  reopen the circuit because of a problem in the caller.
- **A functional error is a success.** A 404 or a validation error proves the dependency is up and
  answering. Keep `ProbeTimeout` above the caller's `AttemptTimeout`, so a slow but successful probe
  is not cut off by its lease.

## Failures outside a probe

Callers learn about state changes from a cache that is up to `SnapshotTtl` (5 s) old. For a few
seconds after the circuit half-opens, a caller may still think it is closed — and calls without a
permit. If such a call fails with a counted failure, the circuit opens again: it is evidence the
dependency is still down. A success outside a probe is recorded but does not count towards closing:
only probes decide that.

## Choosing the numbers

- **`HalfOpenMaxProbes`**: 1 for fragile or expensive dependencies; a few for high-volume ones, where
  a single probe would make recovery slow.
- **`HalfOpenSuccessThreshold`**: 1 closes quickly but trusts a single lucky call; 2–3 is more robust.
- **`ProbeTimeout`**: comfortably above the caller's attempt timeout (default 10 s vs 30 s).

## Switch-driven work while half-open

A [switch](switches.md) cannot hand out permits: a queue subscription is either receiving or not.
While half-open, the switch is closed so work flows again, and the first results decide. Keep it
gentle by limiting consumer concurrency, or let the consumer use the client library per message as
well, so the probe limit applies — see [switches.md](switches.md#half-open-and-switches).
