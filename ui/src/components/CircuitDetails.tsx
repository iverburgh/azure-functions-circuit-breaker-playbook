import type { AlertRuleSuitability, CircuitSnapshot } from "../api/types";
import { formatRelative, formatTimeSpan, formatTimestamp, stateLabels } from "../format";
import { StateBadge } from "./StateBadge";

interface Props {
  circuit: CircuitSnapshot;
  now: number;
  canOperate?: boolean;
  checking?: boolean;
  onCheckAlerts?: (circuit: CircuitSnapshot) => void;
}

const suitabilityLabels: Record<AlertRuleSuitability, string> = {
  Suitable: "suitable",
  UnsupportedType: "ignored: not a log search or metric rule",
  NotFound: "not found or no read access",
  Stateless: "ignored: does not resolve automatically",
  Disabled: "disabled (counts as resolved)",
};

/** The last segment of an Azure resource id: readable, and unique enough within one circuit. */
function ruleName(ruleId: string): string {
  return ruleId.split("/").filter(Boolean).pop() ?? ruleId;
}

export function CircuitDetails({ circuit, now, canOperate = false, checking = false, onCheckAlerts }: Props) {
  const definition = circuit.definition;
  const health = circuit.health;
  const alerts = circuit.alerts;
  return (
    <section className="details" aria-label={`Details of ${circuit.displayName}`}>
      <h2>
        {circuit.displayName} <StateBadge state={circuit.state} />
      </h2>
      <p className="muted">
        <code>{circuit.key}</code> · generation {circuit.generation} · last seen {formatRelative(circuit.lastSeenAt, now)}
      </p>

      <dl className="facts">
        <dt>Opens after</dt>
        <dd>
          {definition.failureThreshold} failures within {formatTimeSpan(definition.failureWindow)}
        </dd>
        <dt>Counted failures</dt>
        <dd>{definition.countedCategories.join(", ")}</dd>
        <dt>Recovery timeout</dt>
        <dd>
          {formatTimeSpan(definition.recoveryTimeout)}, doubling after each failed probe up to {formatTimeSpan(definition.maxRecoveryTimeout)}
          {circuit.consecutiveReopens > 0 && ` (reopened ${circuit.consecutiveReopens}× in a row)`}
        </dd>
        <dt>Probes</dt>
        <dd>
          at most {definition.halfOpenMaxProbes} at a time; {definition.halfOpenSuccessThreshold} successes close the circuit
        </dd>
        <dt>In the window</dt>
        <dd>
          {circuit.successesInWindow} successes, {circuit.failuresInWindow} counted failures, {circuit.ignoredFailuresInWindow} ignored failures
        </dd>
      </dl>

      {circuit.lastFailure && (
        <div className="callout">
          <strong>Last failure</strong> ({circuit.lastFailure.category}, {formatRelative(circuit.lastFailure.at, now)}): {circuit.lastFailure.message}
        </div>
      )}

      {circuit.switch && (
        <div className={circuit.switch.lastError ? "callout callout-error" : "callout"}>
          <strong>Switch</strong> {circuit.switch.kind} <code>{circuit.switch.target}</code>: should be {circuit.switch.desired.toLowerCase()}, is{" "}
          {circuit.switch.applied?.toLowerCase() ?? "unknown"}.
          {circuit.switch.lastError && (
            <>
              {" "}
              Last attempt failed ({circuit.switch.failedAttempts}×): {circuit.switch.lastError}
            </>
          )}
        </div>
      )}

      {health && health.status !== "Healthy" && (
        <div className={health.status === "Unhealthy" ? "callout callout-error" : "callout"}>
          <strong>Health: {health.status.toLowerCase()}</strong>
          <ul>
            {health.reasons.map((reason) => (
              <li key={reason.code}>
                {reason.message} <span className="muted small">({reason.code})</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {alerts && (
        <>
          <h3>Alert rules</h3>
          <p>
            Mode <strong>{alerts.mode.toLowerCase()}</strong>, polled every {formatTimeSpan(alerts.interval)}; last poll {formatRelative(alerts.lastPolledAt, now)}, last
            success {formatRelative(alerts.lastSucceededAt, now)}.
            {alerts.mode === "Shadow" && alerts.wouldOpen && <strong> In active mode this circuit would be open now.</strong>}
          </p>
          {alerts.trippedBy.length > 0 && (
            <div className="callout callout-error">
              <strong>Tripped by</strong> {alerts.trippedBy.map(ruleName).join(", ")}. Fix the cause, then close the alert; the circuit then waits for a person to reset it.
            </div>
          )}
          {alerts.lastError && <div className="callout callout-error">Last poll failed: {alerts.lastError}</div>}
          <ul>
            {alerts.rules.map((rule) => (
              <li key={rule.ruleId} title={rule.ruleId}>
                <code>{ruleName(rule.ruleId)}</code>: {rule.firing ? "firing" : "not firing"}, {suitabilityLabels[rule.suitability]}
              </li>
            ))}
          </ul>
          {canOperate && onCheckAlerts && (
            <button type="button" onClick={() => onCheckAlerts(circuit)} disabled={checking} aria-busy={checking}>
              {checking ? "Checking alerts…" : "Check alerts now"}
            </button>
          )}
        </>
      )}

      <h3>History</h3>
      {circuit.history.length === 0 ? (
        <p className="muted">No transitions yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th scope="col">When</th>
              <th scope="col">Change</th>
              <th scope="col">Trigger</th>
              <th scope="col">Reason</th>
              <th scope="col">By</th>
            </tr>
          </thead>
          <tbody>
            {circuit.history.map((transition) => (
              <tr key={`${transition.at}-${transition.to}`}>
                <td title={formatTimestamp(transition.at)}>{formatRelative(transition.at, now)}</td>
                <td>
                  {stateLabels[transition.from]} → {stateLabels[transition.to]}
                </td>
                <td>{transition.trigger}</td>
                <td>{transition.reason ?? "—"}</td>
                <td>{transition.actor ?? "automatic"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
