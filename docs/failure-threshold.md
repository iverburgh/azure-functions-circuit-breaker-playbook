# Failure threshold

A circuit opens when **`FailureThreshold` counted failures occur within `FailureWindow`**. Both are
part of the circuit's definition, so each dependency gets the sensitivity it needs.

```csharp
new CircuitDefinition
{
    Key = "payments-api",
    DisplayName = "Payments API",
    FailureThreshold = 5,                       // five failures...
    FailureWindow = TimeSpan.FromSeconds(60),   // ...within a minute
    CountedCategories = [FailureCategory.Timeout, FailureCategory.Exception, FailureCategory.ServerError, FailureCategory.Throttled],
}
```

## Why a time window, not consecutive failures

A counter of consecutive failures resets on every success. That has two problems:

- **A flapping dependency never trips it.** Fail, fail, succeed, fail, fail, succeed: half the calls
  fail and the counter never exceeds two.
- **Order matters, so outcomes cannot be batched.** "Three failures and two successes" means
  something different depending on the order they happened in.

Counting failures in a time window has neither problem. A success does not erase recent failures,
and the count does not depend on order — which is what lets callers send one aggregated report per
second instead of one request per call. The test `Successes_do_not_erase_recent_failures` in
[FailureThresholdTests.cs](../tests/CircuitBreaker.Core.Tests/FailureThresholdTests.cs) shows it.

## How the window is kept

[FailureWindow.cs](../src/CircuitBreaker.Core/FailureWindow.cs) keeps ten buckets per window, each
with counts of successes, counted failures and ignored failures. Outcomes are added to the bucket
of the current time slice; buckets older than the window are dropped.

| Property | Value |
|---|---|
| Memory | constant: at most 11 buckets, however many calls are made |
| Accuracy | one bucket, a tenth of the window: with a 60-second window, a failure counts for 60 to 66 seconds |
| Time | the breaker's clock when a report arrives — not the caller's, so clock skew between instances does not matter |

Successes are counted too, though they do not affect the threshold: the console shows them, and a
failure *ratio* could be added later without changing the stored state.

## Choosing the numbers

Start from what the dependency's owners would call an outage, and the volume you send.

| Dependency | Volume | Threshold / window | Why |
|---|---|---|---|
| Payment provider | 50 calls/s | 20 / 30 s | high volume: a short window detects quickly, a higher threshold ignores blips |
| Internal reporting API | 1 call/min | 3 / 10 min | low volume: the window must be long enough to see three calls at all |
| Nightly batch partner | a few calls/day | 2 / 1 h | each call matters; open quickly, recover with probes |

Rules of thumb:

- **The window must contain enough calls** to reach the threshold. With 1 call per minute, a
  threshold of 5 in 60 seconds can never be reached.
- **Lower is not safer.** A threshold of 1 opens on every blip, and each opening costs you a
  recovery timeout of refused calls.
- **Detection latency adds up**: up to `ReportInterval` (1 s) for the report, plus the time to reach
  the threshold, plus `SnapshotTtl` (5 s) until every caller sees the new state. See
  [fail-fast.md](fail-fast.md).

## Which failures count

Only failures in `CountedCategories` count towards the threshold. Functional errors — a validation
error, a 404, a conflict — mean the dependency is healthy and answering; they are recorded as
*ignored failures* and never open the circuit. See [failure-classification.md](failure-classification.md).

## Changing the threshold

Change the definition in code and deploy: the client registers it again on start-up and the entity
picks it up. Changing the *window length* clears the buckets — counts from a differently sized
window would be misleading — so the circuit starts counting afresh.
