# Failure classification

Not every failure means the dependency is unhealthy. A breaker that counts validation errors opens
when a user types a wrong account number; one that ignores timeouts never opens when the dependency
hangs. Classification decides what each outcome means.

## The categories

[Enums.cs](../src/CircuitBreaker.Contracts/Enums.cs):

| Category | Examples | Counts by default |
|---|---|---|
| `Timeout` | attempt timeout exceeded, HTTP 408, `TimeoutException` | yes |
| `Exception` | connection refused, DNS failure, TLS error, socket reset | yes |
| `ServerError` | HTTP 5xx | yes |
| `Throttled` | HTTP 429, HTTP 503 with `Retry-After` | yes |
| `Functional` | HTTP 4xx, validation errors, "not found", "insufficient funds" | **no** |
| *ignored* | the caller cancelled the call | not reported at all |

Classification happens in **two places**, on purpose:

1. **The client classifies** each outcome into a category. It knows the exception, the status code,
   and whether its own timeout fired.
2. **The circuit decides which categories count**, in `CountedCategories`. One dependency may want
   throttling to open the circuit; another may want to ride it out.

Every failure is reported with its category, counted or not. The console shows ignored failures
separately, so "lots of functional errors" stays visible without opening anything.

## The default classifier

[Classification.cs](../src/CircuitBreaker.Client/Classification.cs):

```csharp
public virtual Outcome Classify(Exception exception, bool attemptTimedOut, bool callerCancelled)
{
    if (attemptTimedOut)                                         return Timeout;
    if (callerCancelled && exception is OperationCanceledException) return Ignored;
    if (functionalExceptions.Any(t => t.IsInstanceOfType(exception))) return Functional;
    return exception switch
    {
        TimeoutException => Timeout,
        HttpRequestException { StatusCode: { } status } => FromStatus(status),
        _ => Exception,
    };
}
```

Three distinctions matter most:

- **Our timeout versus their cancellation.** When the circuit's attempt timeout fires, it is a
  `Timeout` failure. When the *caller's* token is cancelled — the user navigated away, the request
  was aborted — the dependency did nothing wrong: the outcome is ignored and not reported.
- **Returned errors count too.** An `HttpClient` returns a 503 rather than throwing. The
  `CircuitBreakerHandler` judges responses with `HttpOutcome.FromResponse`, and `ExecuteAsync` takes a
  result classifier for any API that returns errors as values.
- **Functional exceptions are yours to name.** Pass the types that mean "the dependency is fine, the
  request was not": `new DefaultFailureClassifier(typeof(ValidationException), typeof(NotFoundException))`.

## HTTP status codes

| Status | Category | Why |
|---|---|---|
| 2xx, 3xx | success | |
| 400, 401, 403, 404, 409, 422 | `Functional` | the request was wrong or not allowed; the dependency works |
| 408 | `Timeout` | the server gave up waiting |
| 429 | `Throttled` | slow down |
| 503 with `Retry-After` | `Throttled` | planned unavailability, often load shedding |
| other 5xx | `ServerError` | the dependency is failing |

401 and 403 deserve a thought: if your credentials expired, every call fails — but opening the
circuit does not help, and it hides the real error behind "circuit open". They are functional by
default; alert on them separately.

## Writing your own classifier

Derive from `DefaultFailureClassifier` and override `Classify`, or implement `IFailureClassifier`:

```csharp
public sealed class PaymentsClassifier() : DefaultFailureClassifier(typeof(PaymentDeclinedException))
{
    public override Outcome Classify(Exception exception, bool attemptTimedOut, bool callerCancelled) =>
        exception is PaymentProviderException { Code: "SYSTEM_BUSY" }
            ? Outcome.Failure(FailureCategory.Throttled, exception.Message)
            : base.Classify(exception, attemptTimedOut, callerCancelled);
}
```

Then pass it in the circuit's options: `new CircuitOptions { Definition = ..., Classifier = new PaymentsClassifier() }`.

## How it is tested

[ClassificationTests.cs](../tests/CircuitBreaker.Client.Tests/ClassificationTests.cs) covers attempt
timeouts, caller cancellation, technical and functional exceptions, every status code family and
returned error responses. [FailureThresholdTests.cs](../tests/CircuitBreaker.Core.Tests/FailureThresholdTests.cs)
covers the entity side: functional errors never open a circuit, and each circuit chooses its counted
categories.
