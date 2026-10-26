# Pitfalls

Things that go wrong with distributed circuit breakers and Durable Entities — several of them found
while building this playbook.

## Durable Entities

### Reading entity state is eventually consistent

Signals are queued; the entity applies them a moment later. Reading all entities with a query lags
further still: right after an action, the list can show the old state for seconds. Never conclude
"the action failed" from a list read immediately afterwards. Operations that need an answer —
probe permits, manual actions — go through an orchestration that calls the entity and returns its
reply ([concurrency.md](concurrency.md#writes-that-need-an-answer-and-writes-that-do-not)); the
[console](management-ui.md#lists-lag-behind-actions--on-purpose) keeps that reply until the list
catches up.

### An exception rolls the operation back

The runtime discards the state changes of an operation that throws. A breaker that throws because
it could not flip a switch loses its decision to open. The entity therefore catches switch failures,
records them and retries later ([switches.md](switches.md#when-a-switch-cannot-be-flipped)). Throw
only for genuine bugs.

### Operations run again after a crash

Entities are not replayed like orchestrations, but the operations since the last checkpoint run again
after a crash. Every side effect must be safe to repeat: switches read before they write, the rules
are deterministic given the time, and signals are only sent on commit. The entity harness proves it
([observability-and-testing.md](observability-and-testing.md#the-entity-harness)).

### Timers from the past

A recovery timer scheduled before an operator reset arrives after it. Without a check, it would
half-open a circuit that was just closed and opened again. Every timer carries the generation it was
created in, and is ignored when that no longer matches. Due-time tokens do the same for staleness
checks and switch retries.

### A stray signal creates an entity

Signalling an entity that does not exist creates it. Operations for an unknown circuit therefore
delete the state again instead of storing an empty circuit — otherwise every typo in a key, and
every timer of a deleted circuit, leaves a ghost behind.

### State that grows

Entity state is read and written as one document on every operation. Keep it bounded: the window is
ten buckets, the history twenty transitions, probe leases at most the probe limit. Mark computed
properties `[JsonIgnore]` so they are not stored.

### Reporting every call

An entity handles its operations one at a time. One signal per call puts the breaker's throughput
limit on your traffic. The client aggregates outcomes per second; a window count does not depend on
order, so nothing is lost ([concurrency.md](concurrency.md#throughput-batch-dont-stream)).

### Renaming the task hub

A new task hub name means empty state and abandoned timers. That is a feature when you want a fresh
start ([operations.md](operations.md#starting-with-empty-state)), and an outage of all overrides when
done by accident: isolated circuits are closed again.

## Counting the wrong things

### Functional errors that open the circuit

A 404 for an unknown customer, a validation error, a declined payment: the dependency works. Counting
them opens the circuit for a user's mistake and blocks every other user. Classify them as functional
([failure-classification.md](failure-classification.md)).

### A caller giving up is not a failure

When the caller's request is cancelled — the user navigated away — the dependency did nothing wrong.
The client ignores those, unless the circuit's own attempt timeout fired: that is a timeout.

### Consecutive failures on a busy dependency

"Five failures in a row" never trips on a dependency that fails half of its calls, because the
successes in between reset the counter. Count failures in a time window
([failure-threshold.md](failure-threshold.md#why-a-time-window-not-consecutive-failures)).

## Timeouts and retries

### Retries inside the breaker

A retry policy inside the circuit turns three failed attempts into one outcome — and keeps retrying
when the circuit is open. Put retries outside, and never retry `BrokenCircuitException`
([timeouts-and-retries.md](timeouts-and-retries.md#the-order)).

### The standard resilience handler, unchanged

`AddStandardResilienceHandler` has its own local circuit breaker and retry. Combined unchanged with
this one, the two disagree about the state and the retries multiply
([timeouts-and-retries.md](timeouts-and-retries.md#do-not-use-addstandardresiliencehandler-unchanged)).

### Timeouts that disagree

A probe lease shorter than the attempt timeout cuts off slow successful probes. A breaker that waits
longer for a probe answer than the caller is willing to wait hands out permits nobody uses. The
rules are in one table: [timeouts-and-retries.md](timeouts-and-retries.md#making-timeouts-and-the-breaker-agree).

## The breaker as a dependency

### The breaker becomes the outage

Every call checking the breaker over the network makes the breaker the most critical service you
have. The client decides locally from a cached state, refreshes it at most once per TTL, and lets
calls through when it knows nothing ([fail-fast.md](fail-fast.md#when-the-breaker-itself-is-unreachable)).

### Callers keep a known open state

The flip side: a caller that knew a circuit was open keeps refusing while the breaker is unreachable,
because it cannot get a probe permit. That is deliberate — the dependency was failing — but know it
during a breaker outage ([operations.md](operations.md#the-breaker-itself-is-unavailable)).

## Switches

### Half-open lets all consumers through

A switch has two positions. While half-open it is closed, so every consumer of the subscription
resumes, not one probe. Keep the consumer's concurrency low, or let it take a probe permit per
message ([half-open-probes.md](half-open-probes.md#switch-driven-work-while-half-open)).

### People editing the breaker's feature flags

An operator who enables a flag the breaker disabled fights the breaker, and loses at the next
transition. Give the breaker's flags their own label (`Switches:FeatureFlagLabel`) and make clear
they are managed by the breaker; overrule through the console instead.

## Security

### Trusting the principal header without Easy Auth

The Function reads the caller from `X-MS-CLIENT-PRINCIPAL`. That is safe only when App Service
Authentication is enabled, because the platform strips any copy a client sends. Without it, anyone
can claim the Operator role ([security-and-deployment.md](security-and-deployment.md#how-the-function-knows-who-calls)).
