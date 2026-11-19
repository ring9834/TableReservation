// Run with `npm test` (node --test, which strips the types of zod.ts). zod without eval under the CSP (Design §17.4).
// Each test file runs in its own process, so nothing has imported zod before the spy below is in place.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";

/** Replaces the global Function with a spy that counts calls and constructions (zod's probe and its compiler). */
const spyOnFunction = () => {
  const counter = { calls: 0 };
  const original = globalThis.Function;
  globalThis.Function = new Proxy(original, {
    apply(target, self, args) {
      counter.calls++;
      return Reflect.apply(target, self, args);
    },
    construct(target, args) {
      counter.calls++;
      return Reflect.construct(target, args);
    },
  });
  return { counter, restore: () => (globalThis.Function = original) };
};

test("the wrapper sets jitless and building and parsing a z.object never calls Function", async () => {
  const { counter, restore } = spyOnFunction();
  try {
    const { z } = await import("./zod.ts");

    // zod keeps its global config here (zod/v4/core); z.config writes to it.
    assert.equal(globalThis.__zod_globalConfig?.jitless, true);
    const schema = z.object({ name: z.string(), partySize: z.number().int().min(1) });
    assert.deepEqual(schema.parse({ name: "Ana", partySize: 2 }), { name: "Ana", partySize: 2 });
    assert.equal((await schema.safeParseAsync({ name: "Ana", partySize: 0 })).success, false);

    assert.equal(counter.calls, 0);
  } finally {
    restore();
  }
});

test("the spy would see it: plain zod probes with Function on the first z.object", () => {
  // A fresh process, so this process's jitless setting can't hide the probe.
  const script = `
    const original = globalThis.Function; let calls = 0;
    globalThis.Function = new Proxy(original, {
      apply: (t, s, a) => (calls++, Reflect.apply(t, s, a)),
      construct: (t, a) => (calls++, Reflect.construct(t, a)),
    });
    const { z } = await import("zod");
    z.object({ name: z.string() }).parse({ name: "Ana" });
    console.log(calls);`;
  const result = spawnSync(process.execPath, ["--input-type=module", "-e", script], {
    encoding: "utf8",
  });

  assert.equal(result.status, 0, result.stderr);
  assert.ok(Number(result.stdout.trim()) > 0, "plain zod called Function");
});
