// Run with `npm test` (node --test). The host's "Full screen" button (M12b, Design §17.5).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  canOfferFullscreen,
  enterFullscreen,
  exitFullscreen,
  isFullscreen,
  standaloneQuery,
} from "./fullscreen.mjs";

const browserWindow = ({ standalone = false, iosStandalone = undefined } = {}) => ({
  matchMedia: (query) => ({ matches: query === standaloneQuery && standalone }),
  navigator: iosStandalone === undefined ? {} : { standalone: iosStandalone },
});

test("offered where the standard API is enabled (Chrome, Edge, Safari 16.4+)", () => {
  assert.equal(canOfferFullscreen({ fullscreenEnabled: true }, browserWindow()), true);
});

test("offered with only the webkit-prefixed API (older iPad Safari)", () => {
  assert.equal(canOfferFullscreen({ webkitFullscreenEnabled: true }, browserWindow()), true);
});

test("hidden where neither is enabled (iPhone Safari)", () => {
  assert.equal(canOfferFullscreen({}, browserWindow({ iosStandalone: false })), false);
  assert.equal(
    canOfferFullscreen(
      { fullscreenEnabled: false, webkitFullscreenEnabled: false },
      browserWindow(),
    ),
    false,
  );
});

test("hidden in the installed app (display-mode: standalone)", () => {
  assert.equal(
    canOfferFullscreen({ fullscreenEnabled: true }, browserWindow({ standalone: true })),
    false,
  );
});

test("hidden in an iOS home-screen app (navigator.standalone)", () => {
  assert.equal(
    canOfferFullscreen({ webkitFullscreenEnabled: true }, browserWindow({ iosStandalone: true })),
    false,
  );
});

test("full screen is read from either property", () => {
  const element = {};
  assert.equal(isFullscreen({ fullscreenElement: null }), false);
  assert.equal(isFullscreen({}), false);
  assert.equal(isFullscreen({ fullscreenElement: element }), true);
  assert.equal(isFullscreen({ webkitFullscreenElement: element }), true);
});

test("enter uses the standard method first, the webkit one as a fallback", async () => {
  const calls = [];
  await enterFullscreen({
    requestFullscreen: async () => calls.push("standard"),
    webkitRequestFullscreen: () => calls.push("webkit"),
  });
  await enterFullscreen({ webkitRequestFullscreen: () => calls.push("webkit") });
  assert.deepEqual(calls, ["standard", "webkit"]);
  await assert.rejects(enterFullscreen({}));
});

test("exit uses the standard method first, the webkit one as a fallback", async () => {
  const calls = [];
  await exitFullscreen({
    exitFullscreen: async () => calls.push("standard"),
    webkitExitFullscreen: () => calls.push("webkit"),
  });
  await exitFullscreen({ webkitExitFullscreen: () => calls.push("webkit") });
  await exitFullscreen({});
  assert.deepEqual(calls, ["standard", "webkit"]);
});
