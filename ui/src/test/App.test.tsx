import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { App } from "../App";
import type { CircuitApi } from "../api/circuitApi";
import type { CircuitSnapshot, CircuitState, ManualActionResult } from "../api/types";
import { OperatorRole } from "../api/types";

function snapshot(key: string, state: CircuitState, extra: Partial<CircuitSnapshot> = {}): CircuitSnapshot {
  const allowed: Record<CircuitState, CircuitSnapshot["allowedActions"]> = {
    Closed: ["Isolate", "Disable", "Delete"],
    Open: ["Isolate", "Disable", "Reset", "Delete"],
    HalfOpen: ["Isolate", "Disable", "Reset", "Delete"],
    Isolated: ["Disable", "Reset", "Delete"],
    Disabled: ["Isolate", "Reset", "Delete"],
  };
  return {
    key,
    displayName: key.replace(/-/g, " "),
    state,
    generation: 1,
    stateChangedAt: new Date().toISOString(),
    lastOutcomeAt: null,
    lastSeenAt: new Date().toISOString(),
    failuresInWindow: 1,
    successesInWindow: 10,
    ignoredFailuresInWindow: 0,
    recoveryAt: null,
    consecutiveReopens: 0,
    activeProbes: 0,
    probeSuccesses: 0,
    lastFailure: null,
    switch: null,
    allowedActions: allowed[state],
    history: [],
    definition: {
      key,
      displayName: key,
      failureThreshold: 5,
      failureWindow: "00:01:00",
      countedCategories: ["Timeout", "ServerError"],
      recoveryTimeout: "00:00:30",
      maxRecoveryTimeout: "00:10:00",
      halfOpenMaxProbes: 1,
      halfOpenSuccessThreshold: 2,
      probeTimeout: "00:00:30",
    },
    ...extra,
  };
}

function fakeApi(circuits: CircuitSnapshot[], roles: string[] = [OperatorRole], result?: ManualActionResult) {
  return {
    me: vi.fn().mockResolvedValue({ name: "alice", roles }),
    list: vi.fn().mockResolvedValue(circuits),
    act: vi.fn().mockResolvedValue(result ?? { accepted: true, message: "Circuit changed from Open to Closed." }),
  } satisfies CircuitApi;
}

const circuits = [
  snapshot("payments-api", "Closed"),
  snapshot("shipping-rates", "Open", { recoveryAt: new Date(Date.now() + 30_000).toISOString() }),
  snapshot("legacy-crm", "Isolated"),
];

describe("the circuit console", () => {
  it("lists every circuit with its state as text", async () => {
    render(<App api={fakeApi(circuits)} />);

    const table = await screen.findByRole("table", { name: /circuits and their state/i });
    const rows = within(table).getAllByRole("row");
    expect(rows).toHaveLength(4);
    expect(within(rows[2]!).getByText("Open")).toBeInTheDocument();
    expect(within(rows[2]!).getByText(/probe in 30 s|probe in 29 s/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Open (1)" })).toBeInTheDocument();
  });

  it("shows exactly the actions the breaker allows, and only to operators", async () => {
    render(<App api={fakeApi(circuits)} />);

    await screen.findByRole("button", { name: "Reset shipping rates" });
    expect(screen.queryByRole("button", { name: "Reset payments api" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Isolate payments api" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Isolate legacy crm" })).not.toBeInTheDocument();
  });

  it("is read-only without the operator role", async () => {
    render(<App api={fakeApi(circuits, ["CircuitBreaker.Reader"])} />);

    await screen.findByText("shipping rates");
    expect(screen.getByText(/read-only/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^(Isolate|Disable|Reset|Delete) / })).not.toBeInTheDocument();
  });

  it("asks for a reason before an action and sends it along", async () => {
    const user = userEvent.setup();
    const api = fakeApi(circuits);
    render(<App api={api} />);

    await user.click(await screen.findByRole("button", { name: "Reset shipping rates" }));
    const dialog = screen.getByRole("dialog", { name: /reset shipping rates/i });
    const confirm = within(dialog).getByRole("button", { name: "Reset" });
    expect(confirm).toBeDisabled();

    await user.type(within(dialog).getByLabelText(/reason/i), "Upstream fixed");
    await user.click(confirm);

    expect(api.act).toHaveBeenCalledWith("shipping-rates", "Reset", "Upstream fixed");
    expect(await screen.findByRole("status")).toHaveTextContent("Circuit changed from Open to Closed.");
    await waitFor(() => expect(api.list).toHaveBeenCalledTimes(2));
  });

  it("explains a refused action", async () => {
    const user = userEvent.setup();
    render(<App api={fakeApi(circuits, [OperatorRole], { accepted: false, message: "Reset is not allowed while the circuit is Closed." })} />);

    await user.click(await screen.findByRole("button", { name: "Reset shipping rates" }));
    await user.type(screen.getByLabelText(/reason/i), "Try anyway");
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Reset" }));

    expect(await screen.findByRole("status")).toHaveTextContent("not allowed while the circuit is Closed");
  });

  it("keeps focus inside the dialog and returns it on Escape without acting", async () => {
    const user = userEvent.setup();
    const api = fakeApi(circuits);
    render(<App api={api} />);

    const opener = await screen.findByRole("button", { name: "Isolate payments api" });
    await user.click(opener);
    const reason = screen.getByRole("textbox", { name: /reason/i });
    expect(reason).toHaveFocus();

    await user.tab();
    expect(screen.getByRole("button", { name: "Cancel" })).toHaveFocus();
    await user.tab();
    expect(reason).toHaveFocus();

    await user.keyboard("{Escape}");

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(api.act).not.toHaveBeenCalled();
    expect(opener).toHaveFocus();
  });

  it("filters by state and by text", async () => {
    const user = userEvent.setup();
    render(<App api={fakeApi(circuits)} />);
    await screen.findByText("payments api");

    await user.click(screen.getByRole("button", { name: "Isolated (1)" }));
    expect(screen.queryByText("payments api")).not.toBeInTheDocument();
    expect(screen.getByText("legacy crm")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "All (3)" }));
    await user.type(screen.getByRole("searchbox", { name: /search/i }), "shipping");
    expect(screen.queryByText("legacy crm")).not.toBeInTheDocument();
    expect(screen.getByText("shipping rates")).toBeInTheDocument();
  });

  it("shows history, the last failure and a failing switch in the details", async () => {
    const user = userEvent.setup();
    const detailed = snapshot("search-index", "Disabled", {
      lastFailure: { category: "Timeout", message: "The call exceeded its attempt timeout.", at: new Date().toISOString() },
      switch: { kind: "FeatureFlag", target: "main/search", desired: "Closed", applied: "Open", lastError: "403 Forbidden", lastAttemptAt: null, failedAttempts: 3 },
      history: [{ from: "Open", to: "Disabled", trigger: "Disable", at: new Date().toISOString(), reason: "Too sensitive", actor: "bob" }],
    });
    render(<App api={fakeApi([detailed])} />);

    await user.click(await screen.findByRole("button", { name: "search index" }));

    const details = screen.getByRole("region", { name: /details of search index/i });
    expect(within(details).getByText(/The call exceeded its attempt timeout/)).toBeInTheDocument();
    expect(within(details).getByText(/403 Forbidden/)).toBeInTheDocument();
    expect(within(details).getByRole("cell", { name: "Too sensitive" })).toBeInTheDocument();
    expect(within(details).getByRole("cell", { name: "bob" })).toBeInTheDocument();
    expect(screen.getByText("⚠ failing")).toBeInTheDocument();
  });

  it("reports when the breaker cannot be reached", async () => {
    const api = fakeApi([]);
    api.list.mockRejectedValue(new Error("You are not signed in."));
    render(<App api={api} />);

    expect(await screen.findByRole("alert")).toHaveTextContent("You are not signed in.");
  });
});
