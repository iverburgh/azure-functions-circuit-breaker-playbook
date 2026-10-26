import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { CircuitApi } from "./api/circuitApi";
import type { CallerIdentity, CircuitAction, CircuitSnapshot, CircuitState } from "./api/types";
import { OperatorRole, States } from "./api/types";
import { ActionDialog } from "./components/ActionDialog";
import { CircuitDetails } from "./components/CircuitDetails";
import { CircuitTable } from "./components/CircuitTable";
import { stateLabels } from "./format";
import { mergeRecent, RecentChangeLifetimeMs, type RecentChange } from "./mergeRecent";

interface Props {
  api: CircuitApi;
  refreshIntervalMs?: number;
}

interface PendingAction {
  circuit: CircuitSnapshot;
  action: CircuitAction;
}

export function App({ api, refreshIntervalMs = 10_000 }: Props) {
  const [circuits, setCircuits] = useState<CircuitSnapshot[] | null>(null);
  const [caller, setCaller] = useState<CallerIdentity | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ text: string; ok: boolean } | null>(null);
  const [filter, setFilter] = useState<CircuitState | "All">("All");
  const [search, setSearch] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const recent = useRef(new Map<string, RecentChange>());

  const refresh = useCallback(async () => {
    try {
      setCircuits(mergeRecent(await api.list(), recent.current, Date.now()));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setNow(Date.now());
    }
  }, [api]);

  useEffect(() => {
    api.me().then(setCaller, () => setCaller({ name: null, roles: [] }));
    void refresh();
  }, [api, refresh]);

  useEffect(() => {
    if (!autoRefresh) {
      return;
    }
    const timer = setInterval(() => void refresh(), refreshIntervalMs);
    return () => clearInterval(timer);
  }, [autoRefresh, refresh, refreshIntervalMs]);

  const canOperate = caller?.roles.includes(OperatorRole) ?? false;

  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (circuits ?? []).filter(
      (c) => (filter === "All" || c.state === filter) && (term === "" || c.key.includes(term) || c.displayName.toLowerCase().includes(term)),
    );
  }, [circuits, filter, search]);

  const counts = useMemo(() => {
    const result = new Map<CircuitState, number>();
    for (const c of circuits ?? []) {
      result.set(c.state, (result.get(c.state) ?? 0) + 1);
    }
    return result;
  }, [circuits]);

  const selected = circuits?.find((c) => c.key === selectedKey) ?? null;

  async function confirm(reason: string) {
    if (!pending) {
      return;
    }
    setBusy(true);
    try {
      const result = await api.act(pending.circuit.key, pending.action, reason);
      setNotice({ text: `${pending.circuit.displayName}: ${result.message}`, ok: result.accepted });
      if (result.accepted) {
        recent.current.set(pending.circuit.key, { snapshot: result.circuit ?? null, until: Date.now() + RecentChangeLifetimeMs });
      }
      if (pending.action === "Delete" && result.accepted) {
        setSelectedKey(null);
      }
    } catch (e) {
      setNotice({ text: e instanceof Error ? e.message : String(e), ok: false });
    } finally {
      setBusy(false);
      setPending(null);
      await refresh();
    }
  }

  return (
    <main>
      <header>
        <h1>Circuit breakers</h1>
        <div className="header-tools">
          <span className="muted">
            {caller === null ? "Signing in…" : `${caller.name ?? "Unknown user"} · ${canOperate ? "operator" : "read-only"}`}
          </span>
          <label>
            <input type="checkbox" checked={autoRefresh} onChange={(event) => setAutoRefresh(event.target.checked)} /> Auto-refresh
          </label>
          <button type="button" onClick={() => void refresh()}>
            Refresh
          </button>
        </div>
      </header>

      {error && (
        <div role="alert" className="banner banner-error">
          Could not load circuits: {error}
        </div>
      )}
      {notice && (
        <div role="status" className={notice.ok ? "banner banner-ok" : "banner banner-error"}>
          {notice.text}{" "}
          <button type="button" className="link" onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </div>
      )}

      <nav className="filters" aria-label="Filter by state">
        <button type="button" aria-pressed={filter === "All"} onClick={() => setFilter("All")}>
          All ({circuits?.length ?? 0})
        </button>
        {States.map((state) => (
          <button key={state} type="button" aria-pressed={filter === state} onClick={() => setFilter(state)}>
            {stateLabels[state]} ({counts.get(state) ?? 0})
          </button>
        ))}
        <input type="search" placeholder="Search circuits" aria-label="Search circuits" value={search} onChange={(event) => setSearch(event.target.value)} />
      </nav>

      {circuits === null && !error ? (
        <p aria-busy="true">Loading…</p>
      ) : (
        <CircuitTable
          circuits={visible}
          now={now}
          canOperate={canOperate}
          selectedKey={selectedKey}
          onSelect={(key) => setSelectedKey((current) => (current === key ? null : key))}
          onAction={(circuit, action) => setPending({ circuit, action })}
        />
      )}

      {selected && <CircuitDetails circuit={selected} now={now} />}

      {pending && <ActionDialog circuit={pending.circuit} action={pending.action} busy={busy} onConfirm={(reason) => void confirm(reason)} onCancel={() => setPending(null)} />}
    </main>
  );
}
