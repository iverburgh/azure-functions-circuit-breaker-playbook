import { useEffect, useId, useRef, useState } from "react";
import type { CircuitAction, CircuitSnapshot } from "../api/types";

const consequences: Record<CircuitAction, string> = {
  Isolate: "Every call is refused until an operator resets or disables the circuit. It will not recover on its own.",
  Disable: "Every call is let through, and failures will not open the circuit. Use it when the breaker itself is the problem.",
  Reset: "The circuit closes now with a clean slate: calls flow again without waiting for probes.",
  Delete: "The circuit and its history are removed. Callers that still use it register it again and start closed.",
};

interface Props {
  circuit: CircuitSnapshot;
  action: CircuitAction;
  busy: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}

export function ActionDialog({ circuit, action, busy, onConfirm, onCancel }: Props) {
  const [reason, setReason] = useState("");
  const titleId = useId();
  const reasonId = useId();
  const input = useRef<HTMLTextAreaElement>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const valid = reason.trim().length >= 3;

  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    input.current?.focus();
    return () => opener?.focus();
  }, []);

  const keepFocusInside = (event: React.KeyboardEvent) => {
    const focusable = dialog.current?.querySelectorAll<HTMLElement>("textarea, button:not(:disabled)");
    const first = focusable?.[0];
    const last = focusable?.[focusable.length - 1];
    if (!first || !last) {
      return;
    }
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  return (
    <div className="backdrop">
      <div
        ref={dialog}
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            onCancel();
          } else if (event.key === "Tab") {
            keepFocusInside(event);
          }
        }}
      >
        <h2 id={titleId}>
          {action} {circuit.displayName}?
        </h2>
        <p>{consequences[action]}</p>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !busy) {
              onConfirm(reason.trim());
            }
          }}
        >
          <label htmlFor={reasonId}>Reason (recorded in the history)</label>
          <textarea id={reasonId} ref={input} value={reason} onChange={(event) => setReason(event.target.value)} rows={3} required minLength={3} />
          <div className="dialog-buttons">
            <button type="button" onClick={onCancel}>
              Cancel
            </button>
            <button type="submit" className={action === "Reset" ? "primary" : "danger"} disabled={!valid || busy}>
              {busy ? "Working…" : action}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
