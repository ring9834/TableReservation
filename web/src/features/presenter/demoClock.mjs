// The presenter's demo clock (M11a, Design §17.2). Plain JavaScript with JSDoc types and no imports, so `npm test`
// (node --test) can test it directly.

/** The API's clock modes; each has a word under `presenter.demo.mode`. */
export const clockModes = /** @type {const} */ (["fixed", "running"]);

/** The "+N min" buttons, in minutes. */
export const advanceSteps = /** @type {const} */ ([5, 15]);

/**
 * Business time to show now. Fixed: the API's `at`. Running: `at` plus the real time since the answer was read, so
 * the label ticks between refetches (a change from elsewhere arrives as `demoClockChanged` and refetches).
 * @param {{ mode: string, at: string }} clock the API's answer
 * @param {number} readAtMs when it was read (ms since the epoch)
 * @param {number} nowMs the browser's time now (ms since the epoch)
 * @returns {string} an ISO instant
 */
export const demoNow = (clock, readAtMs, nowMs) =>
  clock.mode === "running"
    ? new Date(Date.parse(clock.at) + Math.max(0, nowMs - readAtMs)).toISOString()
    : clock.at;

/**
 * What the Run / Freeze button switches to.
 * @param {string} mode the current mode
 * @returns {"fixed" | "running"}
 */
export const toggledMode = (mode) => (mode === "running" ? "fixed" : "running");
