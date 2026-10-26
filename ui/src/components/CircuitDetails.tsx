import type { CircuitSnapshot } from "../api/types";
import { formatRelative, formatTimeSpan, formatTimestamp, stateLabels } from "../format";
import { StateBadge } from "./StateBadge";

export function CircuitDetails({ circuit, now }: { circuit: CircuitSnapshot; now: number }) {
  const definition = circuit.definition;
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
