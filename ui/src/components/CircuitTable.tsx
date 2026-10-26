import type { CircuitAction, CircuitSnapshot } from "../api/types";
import { formatRelative } from "../format";
import { StateBadge } from "./StateBadge";

interface Props {
  circuits: CircuitSnapshot[];
  now: number;
  canOperate: boolean;
  selectedKey: string | null;
  onSelect: (key: string) => void;
  onAction: (circuit: CircuitSnapshot, action: CircuitAction) => void;
}

function recovery(circuit: CircuitSnapshot, now: number): string {
  switch (circuit.state) {
    case "Open":
      return `probe ${formatRelative(circuit.recoveryAt, now)}`;
    case "HalfOpen":
      return `${circuit.activeProbes}/${circuit.definition.halfOpenMaxProbes} probing, ${circuit.probeSuccesses}/${circuit.definition.halfOpenSuccessThreshold} ok`;
    case "Isolated":
      return "manual reset needed";
    default:
      return "—";
  }
}

export function CircuitTable({ circuits, now, canOperate, selectedKey, onSelect, onAction }: Props) {
  if (circuits.length === 0) {
    return <p className="muted">No circuits match.</p>;
  }

  return (
    <table className="circuits">
      <caption className="visually-hidden">Circuits and their state</caption>
      <thead>
        <tr>
          <th scope="col">Circuit</th>
          <th scope="col">State</th>
          <th scope="col">Failures in window</th>
          <th scope="col">Recovery</th>
          <th scope="col">Switch</th>
          <th scope="col">Changed</th>
          {canOperate && <th scope="col">Actions</th>}
        </tr>
      </thead>
      <tbody>
        {circuits.map((circuit) => (
          <tr key={circuit.key} className={circuit.key === selectedKey ? "selected" : undefined}>
            <th scope="row">
              <button type="button" className="link" onClick={() => onSelect(circuit.key)} aria-expanded={circuit.key === selectedKey}>
                {circuit.displayName}
              </button>
              <div className="muted small">{circuit.key}</div>
            </th>
            <td>
              <StateBadge state={circuit.state} />
            </td>
            <td>
              <meter min={0} max={circuit.definition.failureThreshold} value={Math.min(circuit.failuresInWindow, circuit.definition.failureThreshold)} aria-hidden="true" />{" "}
              {circuit.failuresInWindow} / {circuit.definition.failureThreshold}
            </td>
            <td>{recovery(circuit, now)}</td>
            <td>{circuit.switch ? (circuit.switch.lastError ? <span className="warning">⚠ failing</span> : circuit.switch.applied === circuit.switch.desired ? "in sync" : "pending") : "—"}</td>
            <td>{formatRelative(circuit.stateChangedAt, now)}</td>
            {canOperate && (
              <td>
                <div className="actions">
                  {circuit.allowedActions.map((action) => (
                    <button key={action} type="button" className={action === "Reset" ? "primary" : action === "Delete" ? "quiet" : "danger"} onClick={() => onAction(circuit, action)} aria-label={`${action} ${circuit.displayName}`}>
                      {action}
                    </button>
                  ))}
                </div>
              </td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
