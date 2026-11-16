// Run with `npm test` (node --test). The SignalR logger of the host screen (Design §18.5).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  classifySignalRLog,
  createSignalRLogger,
  isUnauthorized,
  SignalRLogLevel,
} from "./signalrLogger.mjs";

const { Information, Warning, Error } = SignalRLogLevel;
const stoppedDuringStart =
  "Failed to start the connection: Error: The connection was stopped during negotiation.";
const apiDown = "Failed to complete negotiation with the server: TypeError: Failed to fetch";

const recordingSink = () => {
  const lines = { warn: [], error: [] };
  return { lines, sink: { warn: (m) => lines.warn.push(m), error: (m) => lines.error.push(m) } };
};

test("a start stopped by our own stop() (unmount, Strict Mode remount) is silent", () => {
  assert.equal(classifySignalRLog(Error, stoppedDuringStart, true), "silent");

  const { lines, sink } = recordingSink();
  createSignalRLogger(() => true, sink).log(Error, stoppedDuringStart);
  assert.deepEqual(lines, { warn: [], error: [] });
});

test("the same failure when we did not stop is a connection failure, not an error", () => {
  assert.equal(classifySignalRLog(Error, stoppedDuringStart, false), "outage");
});

test("reconnect failures while the API is down warn once per outage, never as errors", () => {
  const { lines, sink } = recordingSink();
  const logger = createSignalRLogger(() => false, sink);

  logger.log(Error, apiDown);
  logger.log(Error, apiDown);
  logger.log(Error, "Failed to start the connection: Error: Failed to complete negotiation");
  assert.equal(lines.warn.length, 1);
  assert.equal(lines.error.length, 0);

  logger.connected();
  logger.log(Error, apiDown);
  assert.equal(lines.warn.length, 2);
});

test("unexpected errors stay console errors", () => {
  const { lines, sink } = recordingSink();
  const logger = createSignalRLogger(() => true, sink);

  logger.log(
    Error,
    "A callback for the method 'floorplanchanged' threw error 'TypeError: x is undefined'.",
  );
  assert.equal(lines.error.length, 1);
});

test("other warnings are kept and information is dropped", () => {
  const { lines, sink } = recordingSink();
  const logger = createSignalRLogger(() => false, sink);

  logger.log(Warning, "No client method with the name 'somethingNew' found.");
  logger.log(Warning, "No client method with the name 'somethingNew' found.");
  logger.log(Information, "Using HubProtocol 'json'.");
  assert.equal(lines.warn.length, 2);
  assert.equal(lines.error.length, 0);
});

test("a 401 from negotiation is silent and recognised, so the hook goes to sign-in instead of retrying (M11a)", () => {
  const signedOut =
    "Failed to complete negotiation with the server: Error: Unauthorized: Status code '401'";
  assert.equal(classifySignalRLog(Error, signedOut, false), "silent");

  const { lines, sink } = recordingSink();
  const logger = createSignalRLogger(() => false, sink);
  logger.log(Error, signedOut);
  assert.deepEqual(lines, { warn: [], error: [] });

  assert.equal(isUnauthorized(new globalThis.Error("Unauthorized: Status code '401'")), true);
  assert.equal(isUnauthorized(new globalThis.Error("Failed to fetch")), false);
  assert.equal(isUnauthorized(undefined), false);
});
