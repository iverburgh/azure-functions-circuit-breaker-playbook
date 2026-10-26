import type { CircuitState } from "../api/types";
import { stateLabels } from "../format";

const descriptions: Record<CircuitState, string> = {
  Closed: "Calls flow; failures are counted.",
  Open: "Calls are refused until the recovery timeout elapses.",
  HalfOpen: "A few probe calls test whether the dependency has recovered.",
  Isolated: "Forced open by an operator; never recovers on its own.",
  Disabled: "Forced closed by an operator; failures never open it.",
};

/** The state as text and colour — never colour alone. */
export function StateBadge({ state }: { state: CircuitState }) {
  return (
    <span className={`badge badge-${state.toLowerCase()}`} title={descriptions[state]}>
      {stateLabels[state]}
    </span>
  );
}
