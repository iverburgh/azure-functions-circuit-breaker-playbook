import type { CircuitSnapshot } from "./api/types";

/** A snapshot the breaker returned from an action, kept until the list catches up. <c>null</c> marks a deletion. */
export interface RecentChange {
  snapshot: CircuitSnapshot | null;
  until: number;
}

export const RecentChangeLifetimeMs = 30_000;

/**
 * Reading the list of circuits is eventually consistent: for a few seconds after an operation the
 * stored state can still be the old one. The answer to an action comes from the entity itself and is
 * current, so it wins until the list shows the same or a newer generation.
 */
export function mergeRecent(fetched: CircuitSnapshot[], recent: Map<string, RecentChange>, now: number): CircuitSnapshot[] {
  for (const [key, change] of recent) {
    if (change.until <= now) {
      recent.delete(key);
    }
  }

  const merged: CircuitSnapshot[] = [];
  for (const circuit of fetched) {
    const change = recent.get(circuit.key);
    if (!change) {
      merged.push(circuit);
    } else if (change.snapshot === null) {
      // Deleted, but the list does not know yet.
    } else if (change.snapshot.generation > circuit.generation) {
      merged.push(change.snapshot);
    } else {
      recent.delete(circuit.key);
      merged.push(circuit);
    }
  }

  for (const [key, change] of recent) {
    if (change.snapshot === null && !fetched.some((c) => c.key === key)) {
      recent.delete(key);
    }
  }

  return merged;
}
