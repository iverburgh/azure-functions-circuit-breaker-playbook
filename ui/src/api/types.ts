// Mirrors CircuitBreaker.Contracts. The breaker serializes with camelCase names and enums as strings.

export type CircuitState = "Closed" | "Open" | "HalfOpen" | "Isolated" | "Disabled" | "OpenedByAlert" | "OpenedAfterAlert";

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
  alertRuleIds?: string[];
}

export type AlertPollingMode = "Off" | "Shadow" | "Active";

export type AlertRuleSuitability = "Suitable" | "UnsupportedType" | "NotFound" | "Stateless" | "Disabled";

export interface AlertRuleStatus {
  ruleId: string;
  suitability: AlertRuleSuitability;
  firing: boolean;
}

export interface AlertStatus {
  mode: AlertPollingMode;
  active: boolean;
  wouldOpen: boolean;
  trippedBy: string[];
  rules: AlertRuleStatus[];
  lastPolledAt: string | null;
  lastSucceededAt: string | null;
  lastError: string | null;
  interval: string;
}

export type CircuitHealthStatus = "Healthy" | "Degraded" | "Unhealthy";

export interface HealthReason {
  code: number;
  message: string;
}

export interface CircuitHealth {
  status: CircuitHealthStatus;
  reasons: HealthReason[];
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
  /** Absent or null when the circuit links no alert rules. */
  alerts?: AlertStatus | null;
  /** Absent from older breakers: treat as healthy. */
  health?: CircuitHealth;
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

export const States: CircuitState[] = ["Closed", "Open", "HalfOpen", "OpenedByAlert", "OpenedAfterAlert", "Isolated", "Disabled"];
