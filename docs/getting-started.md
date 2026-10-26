# Getting started

Run the whole system on your machine in about ten minutes: storage emulator, the breaker Function,
and the management console.

## Prerequisites

- .NET 10 SDK
- Node.js 24
- [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite), the storage emulator (`npm install -g azurite`)

## 1. Run the tests

```shell
dotnet test CircuitBreaker.slnx
cd ui && npm ci && npm test
```

Everything runs without Azure: the rules are pure functions, the entity runs under a simulated
runtime, and the client talks to a fake API. See [observability-and-testing.md](observability-and-testing.md).

## 2. Look at the console on demo data

```shell
cd ui
npm run dev:mock
```

Open `http://localhost:5173`. The demo has a circuit in every state, a failing switch, and
history. Actions work against the demo data.

## 3. Run the breaker locally

Start the storage emulator, which holds the Durable Functions task hub:

```shell
azurite --silent --location ./.azurite
```

In a second terminal, configure and start the Function:

```shell
cd src/CircuitBreaker.Function
cp local.settings.example.json local.settings.json
func start
```

`local.settings.json` sets `Security__Mode` to `Development`: every request is a fixed user with all
roles, so you need no Entra ID locally. The Function refuses this mode outside the Development
environment.

In a third terminal, start the console against the real Function:

```shell
cd ui
npm run dev
```

## 4. Drive a circuit through its states

Open [CircuitBreaker.http](../src/CircuitBreaker.Function/CircuitBreaker.http) in Visual Studio, Rider
or VS Code with the REST Client extension, and send the requests in order while you watch the
console:

1. Register `demo-api` with a threshold of 3 and a recovery timeout of 15 seconds.
2. Report three server errors: the circuit opens.
3. Report fifty functional errors: they are recorded, but change nothing.
4. Request a probe: refused while open. Wait 15 seconds — the durable timer moves the circuit to
   half-open — and request again: granted. Ask once more: refused, only one probe at a time.
5. Report the probe's success: the circuit closes.
6. Isolate it, try again (409, with the reason), reset it.

The history in the console shows every transition, its trigger and reason, and who did it.

> Lists in the console can lag a few seconds behind an action: reading all circuits is eventually
> consistent. The console shows the answer of the action itself until the list catches up. See
> [pitfalls.md](pitfalls.md#reading-entity-state-is-eventually-consistent).

## 5. Call through a circuit from your own code

```csharp
builder.Services.AddCircuitBreakerClient(options => options.ServiceUri = new Uri("http://localhost:7071/"))
    .AddCircuit(new CircuitOptions
    {
        Definition = new CircuitDefinition { Key = "demo-api", DisplayName = "Demo API" },
        AttemptTimeout = TimeSpan.FromSeconds(5),
    });

builder.Services.AddHttpClient("demo", client => client.BaseAddress = new Uri("https://demo.example.com/"))
    .AddCircuitBreaker("demo-api");
```

Every request through that `HttpClient` now goes through the circuit. The full walkthrough is in
[applying-to-your-solution.md](applying-to-your-solution.md).
