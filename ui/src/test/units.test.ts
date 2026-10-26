import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, HttpCircuitApi } from "../api/circuitApi";
import { MockCircuitApi } from "../api/mockApi";
import { formatDuration, formatRelative, parseTimeSpan } from "../format";

describe("formatting", () => {
  it.each([
    ["00:00:30", 30_000],
    ["00:10:00", 600_000],
    ["1.02:00:00", 93_600_000],
    ["00:00:00.5000000", 500],
  ])("parses the .NET TimeSpan %s", (value, expected) => {
    expect(parseTimeSpan(value)).toBe(expected);
  });

  it.each([
    [45_000, "45 s"],
    [90_000, "1 min 30 s"],
    [7_200_000, "2 h"],
    [259_200_000, "3 d"],
  ])("formats %d ms as %s", (milliseconds, expected) => {
    expect(formatDuration(milliseconds)).toBe(expected);
  });

  it("describes past and future moments", () => {
    const now = Date.parse("2026-01-05T09:00:00Z");
    expect(formatRelative("2026-01-05T08:59:48Z", now)).toBe("12 s ago");
    expect(formatRelative("2026-01-05T09:00:25Z", now)).toBe("in 25 s");
    expect(formatRelative(null, now)).toBe("—");
  });
});

describe("the HTTP client", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("sends the reason and treats 409 as an answer, not an error", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ accepted: false, message: "not allowed" }), { status: 409 }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await new HttpCircuitApi("/api", () => Promise.resolve("token-123")).act("payments-api", "Reset", "Fixed");

    expect(result.accepted).toBe(false);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/circuits/payments-api/actions/reset");
    expect(init.body).toBe(JSON.stringify({ reason: "Fixed" }));
    expect(new Headers(init.headers).get("Authorization")).toBe("Bearer token-123");
  });

  it("turns problem details into a readable error", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: "Invalid request", detail: "A reason is required." }), { status: 400 })));

    await expect(new HttpCircuitApi().act("payments-api", "Isolate", "")).rejects.toEqual(new ApiError(400, "A reason is required."));
  });

  it("explains a missing role", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 403 })));

    await expect(new HttpCircuitApi().list()).rejects.toThrow("You do not have a role that allows this.");
  });
});

describe("the mock API", () => {
  it("imitates the allowed transitions", async () => {
    const api = new MockCircuitApi();

    expect((await api.act("payments-api", "Reset", "x")).accepted).toBe(false);
    const isolated = await api.act("payments-api", "Isolate", "maintenance");
    expect(isolated.circuit?.state).toBe("Isolated");
    expect(isolated.circuit?.allowedActions).toEqual(["Disable", "Reset", "Delete"]);
  });
});
