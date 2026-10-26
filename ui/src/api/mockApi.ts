import type { CircuitApi } from "./circuitApi";
import type { CallerIdentity, CircuitAction, CircuitSnapshot, CircuitState, ManualActionResult } from "./types";
import { OperatorRole } from "./types";

// Demo data for `npm run dev:mock`: explore the console without running the breaker.
// The real rules live in the breaker; this mock only imitates them.
const targets: Record<Exclude<CircuitAction, "Delete">, { to: CircuitState; from: CircuitState[] }> = {
  Isolate: { to: "Isolated", from: ["Closed", "Open", "HalfOpen", "Disabled"] },
  Disable: { to: "Disabled", from: ["Closed", "Open", "HalfOpen", "Isolated"] },
  Reset: { to: "Closed", from: ["Open", "HalfOpen", "Isolated", "Disabled"] },
};

function allowedActions(state: CircuitState): CircuitAction[] {
  const actions = (Object.keys(targets) as Exclude<CircuitAction, "Delete">[]).filter((a) => targets[a].from.includes(state));
  return [...actions, "Delete"];
}

function ago(seconds: number): string {
  return new Date(Date.now() - seconds * 1000).toISOString();
}

function circuit(key: string, displayName: string, state: CircuitState, extra: Partial<CircuitSnapshot> = {}): CircuitSnapshot {
  return {
    key,
    displayName,
    state,
    generation: 4,
    stateChangedAt: ago(300),
    lastOutcomeAt: ago(2),
    lastSeenAt: ago(2),
    failuresInWindow: 0,
    successesInWindow: 812,
    ignoredFailuresInWindow: 3,
    recoveryAt: null,
    consecutiveReopens: 0,
    activeProbes: 0,
    probeSuccesses: 0,
    lastFailure: null,
    switch: null,
    allowedActions: allowedActions(state),
    history: [],
    definition: {
      key,
      displayName,
      failureThreshold: 5,
      failureWindow: "00:01:00",
      countedCategories: ["Timeout", "Exception", "ServerError", "Throttled"],
      recoveryTimeout: "00:00:30",
      maxRecoveryTimeout: "00:10:00",
      halfOpenMaxProbes: 1,
      halfOpenSuccessThreshold: 2,
      probeTimeout: "00:00:30",
    },
    ...extra,
  };
}

export class MockCircuitApi implements CircuitApi {
  private circuits: CircuitSnapshot[] = [
    circuit("payments-api", "Payments API", "Closed", { failuresInWindow: 2 }),
    circuit("shipping-rates", "Shipping rates", "Open", {
      failuresInWindow: 7,
      successesInWindow: 0,
      recoveryAt: new Date(Date.now() + 45_000).toISOString(),
      consecutiveReopens: 1,
      lastFailure: { category: "Timeout", message: "The call exceeded its attempt timeout.", at: ago(20) },
      history: [
        { from: "HalfOpen", to: "Open", trigger: "ProbeFailed", at: ago(20), reason: "Probe failed: Timeout." },
        { from: "Open", to: "HalfOpen", trigger: "RecoveryTimeoutElapsed", at: ago(50), reason: "Recovery timeout elapsed." },
        { from: "Closed", to: "Open", trigger: "FailureThresholdReached", at: ago(80), reason: "5 counted failures within 00:01:00." },
      ],
    }),
    circuit("tax-service", "Tax service", "HalfOpen", { activeProbes: 1, probeSuccesses: 1, successesInWindow: 1 }),
    circuit("legacy-crm", "Legacy CRM", "Isolated", {
      switch: { kind: "ServiceBusSubscription", target: "orders/crm-sync", desired: "Open", applied: "Open", lastError: null, lastAttemptAt: ago(3600), failedAttempts: 0 },
      history: [{ from: "Closed", to: "Isolated", trigger: "Isolate", at: ago(3600), reason: "Vendor maintenance window", actor: "alice@example.com" }],
    }),
    circuit("search-index", "Search index", "Disabled", {
      switch: { kind: "FeatureFlag", target: "main/search-indexing", desired: "Closed", applied: "Open", lastError: "403 Forbidden: missing App Configuration Data Owner role.", lastAttemptAt: ago(40), failedAttempts: 3 },
      history: [{ from: "Open", to: "Disabled", trigger: "Disable", at: ago(900), reason: "Breaker too sensitive during reindex", actor: "bob@example.com" }],
    }),
  ];

  me(): Promise<CallerIdentity> {
    return Promise.resolve({ name: "mock-operator", roles: [OperatorRole] });
  }

  list(): Promise<CircuitSnapshot[]> {
    return Promise.resolve(structuredClone(this.circuits));
  }

  act(key: string, action: CircuitAction, reason: string): Promise<ManualActionResult> {
    const current = this.circuits.find((c) => c.key === key);
    if (!current) {
      return Promise.reject(new Error("The circuit does not exist."));
    }
    if (action === "Delete") {
      this.circuits = this.circuits.filter((c) => c.key !== key);
      return Promise.resolve({ accepted: true, message: "Circuit deleted." });
    }

    const target = targets[action];
    if (!target.from.includes(current.state)) {
      return Promise.resolve({ accepted: false, message: `${action} is not allowed while the circuit is ${current.state}.` });
    }

    const updated: CircuitSnapshot = {
      ...current,
      state: target.to,
      generation: current.generation + 1,
      stateChangedAt: new Date().toISOString(),
      recoveryAt: null,
      activeProbes: 0,
      allowedActions: allowedActions(target.to),
      history: [{ from: current.state, to: target.to, trigger: action, at: new Date().toISOString(), reason, actor: "mock-operator" }, ...current.history],
    };
    this.circuits = this.circuits.map((c) => (c.key === key ? updated : c));
    return Promise.resolve({ accepted: true, message: `Circuit changed from ${current.state} to ${target.to}.`, circuit: updated });
  }
}
