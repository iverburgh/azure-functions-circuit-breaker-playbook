# Applying it to your own solution

From "we have a dependency that fails now and then" to circuits that every instance shares. Each
step is useful on its own.

## 0. Decide whether you need a shared breaker

Use a local, in-process breaker (`Microsoft.Extensions.Http.Resilience`, Polly) when one service with
a few instances calls the dependency, and nobody needs to see or overrule the breaker. Use this one
when several instances or services call the same dependency, when state must survive restarts, when
queue consumers or scheduled jobs must stop, or when operators need control. See the
[README](../README.md#why-not-an-in-process-breaker).

## 1. Deploy the breaker

Follow [security-and-deployment.md](security-and-deployment.md): app registration with three roles,
the Bicep template, publish the Function. One breaker serves all your services; circuits are
identified by key.

## 2. Add the client to your service

Copy [CircuitBreaker.Client](../src/CircuitBreaker.Client) and
[CircuitBreaker.Contracts](../src/CircuitBreaker.Contracts) into your solution, or publish them as
packages to your own feed. Configure the breaker's address:

```json
{
  "CircuitBreaker": {
    "ServiceUri": "https://<function-app-name>.azurewebsites.net/"
  }
}
```

and authentication, with the service's managed identity
([details](security-and-deployment.md#4-let-services-report)):

```csharp
var circuitBreakers = builder.Services.AddCircuitBreakerClient();
circuitBreakers.ApiHttpClient.AddHttpMessageHandler(() => new BearerTokenHandler(GetBreakerTokenAsync));
```

## 3. Define one circuit per dependency

A circuit protects one dependency — a service, a database, a vendor API — not one endpoint. If one
part of a dependency fails independently of the rest (a separate region, a separate backend), give
that part its own circuit.

```csharp
circuitBreakers.AddCircuit(new CircuitOptions
{
    Definition = new CircuitDefinition
    {
        Key = "payment-provider",
        DisplayName = "Payment provider",
        FailureThreshold = 10,
        FailureWindow = TimeSpan.FromSeconds(30),
        RecoveryTimeout = TimeSpan.FromSeconds(30),
    },
    AttemptTimeout = TimeSpan.FromSeconds(5),
    Classifier = new DefaultFailureClassifier(typeof(PaymentDeclinedException)),
});
```

| Setting | Start with | Read |
|---|---|---|
| `FailureThreshold`, `FailureWindow` | enough failures to rule out noise at your normal volume | [failure-threshold.md](failure-threshold.md#choosing-the-numbers) |
| `CountedCategories` | the default: everything but functional errors | [failure-classification.md](failure-classification.md) |
| `RecoveryTimeout`, `MaxRecoveryTimeout` | how long the dependency usually needs to recover | [recovery-timeout.md](recovery-timeout.md#choosing-the-numbers) |
| `HalfOpenMaxProbes`, `HalfOpenSuccessThreshold` | 1 and 2 for a fragile dependency | [half-open-probes.md](half-open-probes.md#choosing-the-numbers) |
| `AttemptTimeout` | just above the normal worst-case latency | [timeouts-and-retries.md](timeouts-and-retries.md#making-timeouts-and-the-breaker-agree) |

The definition lives in the caller's code and is registered on start-up. When several services use
the same circuit, give them the same definition — the last registration wins.

## 4. Guard HTTP calls

```csharp
var payments = builder.Services.AddHttpClient<PaymentClient>(client => client.BaseAddress = new Uri("https://payments.example.com/"));
payments.AddResilienceHandler("retry", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
{
    MaxRetryAttempts = 2,
    ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is not BrokenCircuitException
        && (args.Outcome.Exception is not null || (int)args.Outcome.Result!.StatusCode >= 500)),
}));
payments.AddCircuitBreaker("payment-provider");
```

The retry is added first, so it runs **outside** the circuit: every attempt is judged, and a refused
call is not retried. See [timeouts-and-retries.md](timeouts-and-retries.md#the-order).

## 5. Decide what a refusal means for your users

A `BrokenCircuitException` is not an error to log and rethrow; it is a decision. Choose per use case:

| Option | Example |
|---|---|
| Tell the caller to come back later | `503 Service Unavailable` with `Retry-After` from `RetryAfter` |
| Degrade | show the order without the delivery estimate |
| Defer | store the request and process it when the circuit closes |
| Use an alternative | a second provider, a cached answer |

```csharp
app.MapPost("/payments", async (PaymentRequest request, PaymentClient payments) =>
{
    try
    {
        return Results.Ok(await payments.PayAsync(request));
    }
    catch (BrokenCircuitException refused)
    {
        var retryAfter = refused.RetryAfter is { } at ? Math.Max(1, (int)(at - DateTimeOffset.UtcNow).TotalSeconds) : 60;
        return Results.Problem("Payments are temporarily unavailable.", statusCode: 503,
            extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter });
    }
});
```

## 6. Guard other calls

For an SDK, a database or a gRPC client, wrap the call:

```csharp
public sealed class InventoryService([FromKeyedServices("inventory-db")] ICircuitBreaker circuit, InventoryDb db)
{
    public Task<int> CountAsync(string sku, CancellationToken cancellationToken) =>
        circuit.ExecuteAsync(token => db.CountAsync(sku, token), cancellationToken);
}
```

When an API returns errors instead of throwing them, judge the result too with the overload that
takes `classifyResult`. For full control, use `AcquireAsync` and report on the permit yourself.

## 7. Stop queue consumers and scheduled jobs

Give the circuit a [switch](switches.md): a feature flag that the job checks, or the subscription the
consumer reads. Report the outcome of each message or run with `ExecuteAsync`, so the circuit sees
the failures. While half-open, the switch lets work through; see
[half-open-probes.md](half-open-probes.md#switch-driven-work-while-half-open).

## 8. Watch it

Add the alerts from [observability-and-testing.md](observability-and-testing.md#alerts-worth-having)
and give operators the Reader or Operator role in the [console](management-ui.md). Introduce each new
circuit as a dry run first: [operations.md](operations.md#introducing-a-new-circuit-safely).

## 9. Test it

- **Your refusal handling**: replace `ICircuitBreaker` with a fake that throws
  `BrokenCircuitException`, and check what your users get.
- **Your classification**: one test per exception type and status code you care about, like
  [ClassificationTests.cs](../tests/CircuitBreaker.Client.Tests/ClassificationTests.cs).
- **The real thing**: in a test environment, isolate the circuit from the console during a load test,
  then reset it. Callers should refuse at once, and recover without restarts.

## Checklist

- [ ] One circuit per dependency, with a stable key.
- [ ] Functional errors are classified as functional, so they never open the circuit.
- [ ] Retries run outside the circuit and do not retry `BrokenCircuitException`.
- [ ] `AttemptTimeout` < `ProbeTimeout`, and the total timeout leaves room for the retries.
- [ ] Every refusal has a deliberate answer for the user.
- [ ] Queue consumers and scheduled jobs stop through a switch.
- [ ] Alerts on circuits opening, staying isolated, and switches failing.
- [ ] Each new circuit started as a dry run.
