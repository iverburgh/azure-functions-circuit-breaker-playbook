# Fail-fast

When a circuit is open, a call must be refused **at once**: no connection attempt, no timeout to sit
through, no request to the breaker either. That is the whole point — the caller gets an answer in
microseconds and the failing dependency gets breathing room.

## How the client refuses

[CircuitBreakerGate.cs](../src/CircuitBreaker.Client/CircuitBreakerGate.cs) keeps a copy of each
circuit's state, refreshed at most every `SnapshotTtl` (5 s by default), and decides locally:

| Cached state | Decision | Network calls |
|---|---|---|
| `Closed`, `Disabled` | call | none (besides the refresh every 5 s) |
| `Open`, recovery time not reached | throw `BrokenCircuitException` | none |
| `Open`, recovery time passed | ask for a probe permit | one, to the breaker |
| `HalfOpen` | ask for a probe permit | one, to the breaker |
| `Isolated` | throw `BrokenCircuitException` | none |

`BrokenCircuitException` tells the caller which circuit refused, its state, and — when known — when
to try again (`RetryAfter`). Map it to a fast, honest answer:

```csharp
catch (BrokenCircuitException refused)
{
    return Results.Problem(
        title: "Payments are temporarily unavailable",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        extensions: new Dictionary<string, object?> { ["retryAfter"] = refused.RetryAfter });
}
```

Or degrade: serve a cached price list, queue the order for later, hide the "pay now" button.

## Only one caller refreshes

When the cached state expires, one call refreshes it; concurrent calls keep using the stale copy
instead of queueing behind the refresh. A burst of thousands of requests causes one request to the
breaker, not thousands.

## When the breaker itself is unreachable

The breaker must not become the outage it is meant to prevent.

| Situation | Behaviour |
|---|---|
| unreachable, a state was known | keep using the last known state, retry after one TTL |
| unreachable, nothing known yet | `WhenBreakerUnavailable`: `Allow` (default) lets calls through; `Reject` refuses |
| half-open, but no probe permit could be obtained | refuse: the dependency was failing moments ago |
| the breaker does not know the circuit (404) | let calls through, and register the circuit |

Outcomes that could not be reported are kept and sent with the next report, for at most
`MaxReportAge` (30 s); older outcomes would distort the window and are dropped.

## Detection latency

Fail-fast starts once every caller knows the circuit is open. The time from the first failure to
"everybody refuses" is at most:

```text
time to reach the threshold + ReportInterval (1 s) + SnapshotTtl (5 s)
```

Lower `SnapshotTtl` for faster propagation at the cost of more reads; each instance reads each
circuit at most once per TTL. Reads come from persisted entity state and do not queue behind the
entity's operations.

## Work that does not go through the client

A queue consumer pulls messages; a scheduled job wakes up on a timer. For those, refusing call by
call is wasteful: the consumer would pull every message, fail it, and retry it until it is
dead-lettered. A [switch](switches.md) stops the work at the source instead — the subscription stops
delivering, or the job sees its feature flag disabled.

## How it is tested

[FailFastTests.cs](../tests/CircuitBreaker.Client.Tests/FailFastTests.cs):

- `An_open_circuit_refuses_at_once_without_calling_the_dependency` — and without asking the breaker;
- `A_closed_circuit_runs_the_call_and_reads_the_state_once_per_ttl` — 100 calls, one read;
- `When_the_breaker_becomes_unreachable_the_last_known_state_still_applies` — and 20 concurrent calls
  do not hammer the unreachable breaker;
- the `Allow` and `Reject` behaviours, half-open without a permit, and unknown circuits.

[RetryCompositionTests.cs](../tests/CircuitBreaker.Client.Tests/ReportingTests.cs) shows an open
circuit behind a retry policy: one `BrokenCircuitException`, zero calls to the dependency, no retries.
