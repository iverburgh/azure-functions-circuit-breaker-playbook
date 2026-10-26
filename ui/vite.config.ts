import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // The Function runs on 7071 locally; proxying avoids CORS and mirrors a same-origin deployment.
    proxy: { "/api": "http://localhost:7071" },
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
  },
});
