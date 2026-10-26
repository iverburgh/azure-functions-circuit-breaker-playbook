# Security and deployment

## What gets deployed

[infra/main.bicep](../infra/main.bicep) creates, in one resource group:

| Resource | Why |
|---|---|
| Function App on Flex Consumption (`FC1`, .NET 10 isolated) | the breaker; scales to zero, scale-out capped by `maximumInstanceCount` |
| Storage account | the Durable Functions task hub (entity state, queues, timers) and the deployment package |
| Log Analytics workspace and Application Insights | logs from the host, metrics from the worker |
| App Service Authentication (Easy Auth) with Entra ID | validates every token before the Function sees the request |
| Role assignments | the Function's managed identity on the storage account, and optionally on switch targets |

There are no keys or connection strings: storage is reached with the managed identity
(`AzureWebJobsStorage__accountName`, shared key access disabled), and so are switch targets.

## 1. Register the API in Entra ID

The breaker API is one app registration. It defines the roles; Easy Auth uses its client ID as the
token audience.

1. Create an app registration, for example "Circuit breaker API". Note its **client ID**.
2. Under **Expose an API**, set the Application ID URI to `api://<client-id>`. Add a delegated scope,
   for example `access`, if people will use the [management console](management-ui.md).
3. Under **App roles**, create the three roles:

| Value | Allowed member types | For |
|---|---|---|
| `CircuitBreaker.Reporter` | Applications | services that call through circuits |
| `CircuitBreaker.Reader` | Users/Groups | people who watch circuits |
| `CircuitBreaker.Operator` | Users/Groups | people who isolate, disable, reset and delete |

4. Assign Reader and Operator to groups on the API's **enterprise application**, rather than to
   individual people.

## 2. Deploy the infrastructure

Fill in [main.bicepparam](../infra/main.bicepparam) — at least `name` and `apiClientId` — and deploy:

```shell
az group create --name rg-circuit-breaker --location westeurope
az deployment group create --resource-group rg-circuit-breaker --parameters infra/main.bicepparam
```

| Parameter | Meaning |
|---|---|
| `name` | short name used in resource names, such as `cb-prod` |
| `apiClientId` | client ID of the app registration from step 1 |
| `appConfigurationStoreName` | optional: an App Configuration store whose feature flags the breaker may flip |
| `serviceBusNamespaceName` | optional: a Service Bus namespace whose subscriptions the breaker may enable and disable |
| `allowedOrigins` | optional: where the console is hosted, if not on the API's origin |
| `taskHubName` | the Durable Functions task hub; a new name starts with empty state |
| `maximumInstanceCount` | scale-out limit |

## 3. Publish the Function

```shell
cd src/CircuitBreaker.Function
func azure functionapp publish <functionAppName>
```

`functionAppName` is an output of the deployment. Any pipeline that produces a zip package works as
well; Flex Consumption takes it from the deployment container the template creates.

## 4. Let services report

A calling service authenticates with its own managed identity and needs the `CircuitBreaker.Reporter`
app role. App roles for managed identities are assigned through Microsoft Graph:

```shell
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<service-principal-id-of-the-caller>/appRoleAssignments" \
  --body '{"principalId":"<service-principal-id-of-the-caller>","resourceId":"<service-principal-id-of-the-api>","appRoleId":"<id-of-the-reporter-role>"}'
```

In the caller, add a token handler to the client's HTTP client:

```csharp
var credential = new DefaultAzureCredential();
var scope = new TokenRequestContext(["api://<api-client-id>/.default"]);

builder.Services.AddCircuitBreakerClient()
    .ApiHttpClient.AddHttpMessageHandler(() =>
        new BearerTokenHandler(async cancellationToken => (await credential.GetTokenAsync(scope, cancellationToken)).Token));
```

Azure.Identity caches managed identity tokens, so this does not fetch a token per request.

## Roles

The Function checks roles on every request; the console only hides what you may not do.

| Endpoint | Reporter | Reader | Operator |
|---|---|---|---|
| `PUT /api/circuits/{key}` — register | ✓ | | |
| `POST /api/circuits/{key}/outcomes` — report | ✓ | | |
| `POST /api/circuits/{key}/probes` — request a probe permit | ✓ | | |
| `GET /api/circuits/{key}` — one circuit | ✓ | ✓ | ✓ |
| `GET /api/circuits` — all circuits | | ✓ | ✓ |
| `POST /api/circuits/{key}/actions/{action}` — isolate, disable, reset, delete | | | ✓ |
| `GET /api/me` — who am I | any authenticated caller |

## How the Function knows who calls

Easy Auth validates the bearer token (issuer, audience, signature, lifetime), rejects anything else
with `401`, and passes the caller on in the `X-MS-CLIENT-PRINCIPAL` header.
[RequestAuthorizer.cs](../src/CircuitBreaker.Function/Http/RequestAuthorizer.cs) reads the roles and
the name from that header.

> **That header is only trustworthy behind Easy Auth.** With authentication enabled, the platform
> strips any copy a client sends. Without it, anyone could send one. The template always enables
> it; never deploy the Function without.

`Security:Mode = Development` makes every request a fixed user with all roles, for local runs. The
Function refuses to start in that mode outside the Development environment.

## Permissions for switches

| Switch | Role on the target | Why not less |
|---|---|---|
| Feature flag | App Configuration Data Owner | the data plane only allows writes to key-values with this role |
| Service Bus subscription | Azure Service Bus Data Owner | changing a subscription's status is a management operation on the namespace |

Both are broad. Put the flags the breaker manages in a store, and the subscriptions in a namespace,
where that breadth is acceptable — or grant the role on a dedicated store or namespace.

## Hardening further

The template keeps the breaker reachable over the public endpoint, protected by Entra ID. For a
private setup add VNet integration and private endpoints for the Function and the storage account;
nothing in the code changes. Keep in mind that every calling service and the console must then reach
the breaker over the private network.
