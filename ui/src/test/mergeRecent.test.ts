import { describe, expect, it } from "vitest";
import type { CircuitSnapshot } from "../api/types";
import { mergeRecent, type RecentChange } from "../mergeRecent";

const circuit = (key: string, generation: number, state: CircuitSnapshot["state"] = "Closed") => ({ key, generation, state }) as CircuitSnapshot;

describe("merging an action's answer with an eventually consistent list", () => {
  it("prefers the action's newer snapshot while the list lags", () => {
    const recent = new Map<string, RecentChange>([["a", { snapshot: circuit("a", 5, "Closed"), until: 1_000 }]]);

    const merged = mergeRecent([circuit("a", 4, "Open"), circuit("b", 1)], recent, 0);

    expect(merged.map((c) => c.state)).toEqual(["Closed", "Closed"]);
    expect(recent.has("a")).toBe(true);
  });

  it("lets go once the list has caught up", () => {
    const recent = new Map<string, RecentChange>([["a", { snapshot: circuit("a", 5), until: 1_000 }]]);

    mergeRecent([circuit("a", 5)], recent, 0);

    expect(recent.size).toBe(0);
  });

  it("hides a deleted circuit until the list no longer has it", () => {
    const recent = new Map<string, RecentChange>([["a", { snapshot: null, until: 1_000 }]]);

    expect(mergeRecent([circuit("a", 4), circuit("b", 1)], recent, 0).map((c) => c.key)).toEqual(["b"]);
    expect(mergeRecent([circuit("b", 1)], recent, 10)).toHaveLength(1);
    expect(recent.size).toBe(0);
  });

  it("forgets old changes, so a circuit registered again after deletion reappears", () => {
    const recent = new Map<string, RecentChange>([["a", { snapshot: null, until: 1_000 }]]);

    expect(mergeRecent([circuit("a", 0)], recent, 1_000).map((c) => c.key)).toEqual(["a"]);
  });
});
