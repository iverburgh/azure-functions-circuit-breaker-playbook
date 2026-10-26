// Mirrors CircuitBreaker.Contracts. The breaker serializes with camelCase names and enums as strings.

export type CircuitState = "Closed" | "Open" | "HalfOpen" | "Isolated" | "Disabled";

export type FailureCategory = "Timeout" | "Exception" | "ServerError" | "Throttled" | "Functional";

export type CircuitAction = "Isolate" | "Disable" | "Reset" | "Delete";

export type SwitchPosition = "Open" | "Closed";

export interface FailureSample {
  category: FailureCategory;
  message: string;
  at: string;
}

export interface TransitionRecord {
  from: CircuitState;
  to: CircuitState;
  trigger: string;
  at: string;
  reason?: string | null;
  actor?: string | null;
}

export interface SwitchStatus {
  kind: "FeatureFlag" | "ServiceBusSubscription";
  target: string;
  desired: SwitchPosition;
  applied: SwitchPosition | null;
  lastError: string | null;
  lastAttemptAt: string | null;
  failedAttempts: number;
}

export interface CircuitDefinition {
  key: string;
  displayName: string;
  failureThreshold: number;
  failureWindow: string;
  countedCategories: FailureCategory[];
  recoveryTimeout: string;
  maxRecoveryTimeout: string;
  halfOpenMaxProbes: number;
  halfOpenSuccessThreshold: number;
  probeTimeout: string;
}

export interface CircuitSnapshot {
  key: string;
  displayName: string;
  state: CircuitState;
  generation: number;
  stateChangedAt: string;
  lastOutcomeAt: string | null;
  lastSeenAt: string;
  failuresInWindow: number;
  successesInWindow: number;
  ignoredFailuresInWindow: number;
  recoveryAt: string | null;
  consecutiveReopens: number;
  activeProbes: number;
  probeSuccesses: number;
  lastFailure: FailureSample | null;
  switch: SwitchStatus | null;
  allowedActions: CircuitAction[];
  history: TransitionRecord[];
  definition: CircuitDefinition;
}

export interface CallerIdentity {
  name: string | null;
  roles: string[];
}

export interface ManualActionResult {
  accepted: boolean;
  message: string;
  circuit?: CircuitSnapshot | null;
}

export const OperatorRole = "CircuitBreaker.Operator";

export const States: CircuitState[] = ["Closed", "Open", "HalfOpen", "Isolated", "Disabled"];
