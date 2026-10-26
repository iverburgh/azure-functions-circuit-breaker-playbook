import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { HttpCircuitApi } from "./api/circuitApi";
import { MockCircuitApi } from "./api/mockApi";
import "./styles.css";

// Plug in a token provider here when the API requires Entra ID, for example with @azure/msal-browser:
//   new HttpCircuitApi(import.meta.env.VITE_API_BASE_URL, () => acquireTokenSilent(...))
const api = import.meta.env.VITE_API_MODE === "mock" ? new MockCircuitApi() : new HttpCircuitApi(import.meta.env.VITE_API_BASE_URL ?? "/api");

const root = document.getElementById("root");
if (!root) {
  throw new Error("Missing #root element.");
}

createRoot(root).render(
  <StrictMode>
    <App api={api} />
  </StrictMode>,
);
