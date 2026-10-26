import type { CallerIdentity, CircuitAction, CircuitSnapshot, ManualActionResult } from "./types";

export interface CircuitApi {
  me(): Promise<CallerIdentity>;
  list(): Promise<CircuitSnapshot[]>;
  act(key: string, action: CircuitAction, reason: string): Promise<ManualActionResult>;
}

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

/** Talks to the breaker's HTTP API. Pass getToken when the API sits behind Entra ID (for example with MSAL). */
export class HttpCircuitApi implements CircuitApi {
  private readonly baseUrl: string;
  private readonly getToken?: () => Promise<string>;

  constructor(baseUrl = "/api", getToken?: () => Promise<string>) {
    this.baseUrl = baseUrl;
    this.getToken = getToken;
  }

  me(): Promise<CallerIdentity> {
    return this.request<CallerIdentity>("/me");
  }

  list(): Promise<CircuitSnapshot[]> {
    return this.request<CircuitSnapshot[]>("/circuits");
  }

  act(key: string, action: CircuitAction, reason: string): Promise<ManualActionResult> {
    // 409 means "not allowed in this state": a normal answer, not an error.
    return this.request<ManualActionResult>(
      `/circuits/${encodeURIComponent(key)}/actions/${action.toLowerCase()}`,
      { method: "POST", body: JSON.stringify({ reason }) },
      [409],
    );
  }

  private async request<T>(path: string, init: RequestInit = {}, acceptedErrors: number[] = []): Promise<T> {
    const headers = new Headers(init.headers);
    headers.set("Accept", "application/json");
    if (init.body) {
      headers.set("Content-Type", "application/json");
    }
    if (this.getToken) {
      headers.set("Authorization", `Bearer ${await this.getToken()}`);
    }

    const response = await fetch(`${this.baseUrl}${path}`, { ...init, headers });
    if (response.ok || acceptedErrors.includes(response.status)) {
      return (await response.json()) as T;
    }

    throw new ApiError(response.status, await describe(response));
  }
}

async function describe(response: Response): Promise<string> {
  if (response.status === 401) {
    return "You are not signed in.";
  }
  if (response.status === 403) {
    return "You do not have a role that allows this.";
  }
  try {
    const problem = (await response.json()) as { title?: string; detail?: string };
    return problem.detail ?? problem.title ?? `Request failed with status ${response.status}.`;
  } catch {
    return `Request failed with status ${response.status}.`;
  }
}
