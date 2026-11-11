import { defineConfig } from "orval";

// Generates the API client from the committed contract (docs/api/openapi.json), never from a running API, so the
// output only changes when the contract does. CI regenerates and fails on any difference (Design §18.4).
export default defineConfig({
  reservationApi: {
    input: { target: "../docs/api/openapi.json" },
    output: {
      mode: "tags-split",
      target: "src/api/endpoints",
      schemas: "src/api/model",
      client: "react-query",
      httpClient: "fetch",
      clean: true,
      // Header parameters (Idempotency-Key) become function arguments, so each submission can pass its own key.
      headers: true,
      override: {
        mutator: { path: "src/lib/apiFetch.ts", name: "apiFetch" },
        fetch: { includeHttpResponseReturnType: false },
        query: { signal: true },
      },
    },
  },
  reservationZod: {
    input: { target: "../docs/api/openapi.json" },
    output: {
      mode: "tags-split",
      target: "src/api/zod",
      client: "zod",
      fileExtension: ".zod.ts",
      clean: true,
    },
  },
});
