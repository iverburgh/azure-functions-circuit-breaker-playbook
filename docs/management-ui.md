# Management console

A small web console to see every circuit and overrule the automatic decisions. It lives in
[ui/](../ui): React, TypeScript and Vite, with no component library, so it is easy to restyle or
fold into an existing admin portal.

## What it shows

| Where | What |
|---|---|
| Table | every circuit: state (colour **and** text), failures in the window against the threshold, recovery countdown while open, switch status, time of the last change |
| Filter and search | by state, and by key or display name |
| Details | definition (threshold, window, timeouts, probe limits, counted categories), the last failure with its category, probes in flight, switch target and last error, and the history of transitions with trigger, reason and actor |
| Header | who you are and whether you may act, auto-refresh (every 10 seconds) and a manual refresh |

## What it lets you do

Operators get one button per action **the breaker allows in the current state**: the snapshot of
every circuit carries `allowedActions`, computed from the
[transition table](state-machine.md#manual-actions-follow-from-the-table). The console implements no
rules of its own, so it can never offer something the breaker would refuse.

| Action | Effect | Typical use |
|---|---|---|
| Isolate | forced open; no automatic recovery | incident, migration, restore: "do not touch it until I say so" |
| Disable | forced closed; outcomes never open it | the breaker misbehaves, or errors should pass through during maintenance |
| Reset | back to `Closed`, clean window, no backoff | the dependency is fixed and you do not want to wait for probes |
| Delete | removes the circuit | a circuit that no longer exists; it returns if a caller registers it again |

Every action asks for a reason. It is required by the API, stored in the circuit's history with
your name, and logged as a structured event — the audit trail for "who closed this, and why?".

A refused action (the state changed meanwhile, or another operator was faster) comes back as `409`
with an explanation, and is shown as a notice rather than an error.

## Roles

The console asks the API who you are (`GET /api/me`) and shows the action buttons only to callers
with `CircuitBreaker.Operator`; `CircuitBreaker.Reader` sees everything read-only. Hiding buttons is
convenience: the API checks the role on every request. See
[security-and-deployment.md](security-and-deployment.md#roles).

## Lists lag behind actions — on purpose

The list of all circuits comes from an entity query, which is eventually consistent: right after an
action it may still show the old state for a few seconds. The console therefore keeps the snapshot
the action itself returned until the list reports the same or a newer generation
([mergeRecent.ts](../ui/src/mergeRecent.ts)). See
[pitfalls.md](pitfalls.md#reading-entity-state-is-eventually-consistent).

## Running it

| Command | Talks to |
|---|---|
| `npm run dev:mock` | built-in demo data: a circuit in every state, a failing switch, history |
| `npm run dev` | `/api`, proxied by Vite to the Function on `http://localhost:7071` |
| `npm run build` | type-checks and writes a static site to `ui/dist` |

## Hosting it

The build output is static files. Host them anywhere — Azure Static Web Apps, a storage account
static website, or behind the same reverse proxy as your other admin tools.

1. **Get a token for the API.** Register the console as a single-page application in Entra ID and
   let it request a delegated scope of the breaker API (for example `api://<api-client-id>/access`).
   Pass a token provider to `HttpCircuitApi` in [main.tsx](../ui/src/main.tsx), for example with
   `@azure/msal-browser`'s `acquireTokenSilent`. Assign people the Reader or Operator app role on the
   API's enterprise application.
2. **Point it at the API.** Set `VITE_API_BASE_URL` at build time to the Function's URL plus `/api`,
   or serve the console and the API from one origin through a reverse proxy and keep `/api`.
3. **Allow the origin.** With different origins, add the console's URL to `allowedOrigins` in
   [main.bicepparam](../infra/main.bicepparam).

## Accessibility

Native tables with row and column headers, buttons with descriptive labels ("Isolate Payments API"),
state shown as text as well as colour, a modal dialog that traps focus, closes on Escape and
returns focus to the button that opened it, and a live region for notices.

## Tests

[App.test.tsx](../ui/src/test/App.test.tsx) runs the console against an in-memory API with Testing
Library: rendering per state, read-only versus operator, the reason being required, a refused
action, keyboard focus in the dialog, filtering, and keeping an action's result while the list lags
behind.
