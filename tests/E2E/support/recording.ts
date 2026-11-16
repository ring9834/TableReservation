/**
 * The fallback recording of the demo script (M11b, Design §17.3): DEMO_RECORD=1 records a video of each of the three
 * screens at a watchable pace (slowMo DEMO_SLOWMO ms, default 400) into tests/E2E/demo-recordings/ (git-ignored).
 * Off by default and always off in CI. While recording, the QA-3 / QA-4 timings are reported but not enforced: slowMo
 * slows every action down on purpose.
 */
export const recording = process.env.DEMO_RECORD === "1" && !process.env.CI;

export const slowMoMs = recording ? Number(process.env.DEMO_SLOWMO ?? 400) || 400 : 0;
