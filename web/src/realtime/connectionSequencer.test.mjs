// Run with `npm test` (node --test). The SignalR connection's start and stop never overlap (M11b, Design §17.3).
import assert from "node:assert/strict";
import { test } from "node:test";
import { createConnectionSequencer } from "./connectionSequencer.mjs";

/** A connection whose start and stop finish only when the test says so, recording what happened in order. */
const fakeConnection = () => {
  const events = [];
  const pending = { start: [], stop: [] };
  const operation = (name) => () =>
    new Promise((resolve, reject) => {
      events.push(`${name} begins`);
      pending[name].push({
        resolve: () => {
          events.push(`${name} ends`);
          resolve();
        },
        reject: (error) => {
          events.push(`${name} fails`);
          reject(error);
        },
      });
    });
  return { events, pending, start: operation("start"), stop: operation("stop") };
};

const tick = () => new Promise((resolve) => setImmediate(resolve));

test("a stop asked for while a start is in flight waits until the start has settled", async () => {
  const connection = fakeConnection();
  const sequencer = createConnectionSequencer(connection);

  const started = sequencer.start();
  const stopped = sequencer.stop();
  await tick();
  assert.deepEqual(
    connection.events,
    ["start begins"],
    "stop must not reach the connection during a start",
  );
  assert.equal(sequencer.isStarting(), true);
  assert.equal(sequencer.isStopping(), true);

  connection.pending.start[0].resolve();
  await started;
  await tick();
  connection.pending.stop[0].resolve();
  await stopped;

  assert.deepEqual(connection.events, ["start begins", "start ends", "stop begins", "stop ends"]);
  assert.equal(sequencer.isStarting(), false);
  assert.equal(sequencer.isStopping(), false);
});

test("a start that fails still lets the waiting stop run afterwards", async () => {
  const connection = fakeConnection();
  const sequencer = createConnectionSequencer(connection);

  const started = sequencer.start();
  const stopped = sequencer.stop();
  await tick();
  connection.pending.start[0].reject(new Error("offline"));
  await assert.rejects(started, /offline/);
  await tick();
  connection.pending.stop[0].resolve();
  await stopped;

  assert.deepEqual(connection.events, ["start begins", "start fails", "stop begins", "stop ends"]);
});

test("a start asked for while a stop is in flight begins only after the stop", async () => {
  const connection = fakeConnection();
  const sequencer = createConnectionSequencer(connection);

  const stopped = sequencer.stop();
  const started = sequencer.start();
  await tick();
  assert.deepEqual(connection.events, ["stop begins"]);

  connection.pending.stop[0].resolve();
  await stopped;
  await tick();
  connection.pending.start[0].resolve();
  await started;

  assert.deepEqual(connection.events, ["stop begins", "stop ends", "start begins", "start ends"]);
});

test("offline then online during a start: start, then stop, then a new start, one at a time", async () => {
  const connection = fakeConnection();
  const sequencer = createConnectionSequencer(connection);

  const first = sequencer.start(); // a start in flight
  const stopped = sequencer.stop(); // the browser went offline
  const second = sequencer.start(); // and came back: a new start after the stop, not the first one's promise
  assert.notEqual(second, first);

  await tick();
  connection.pending.start[0].reject(new Error("offline"));
  await assert.rejects(first);
  await tick();
  connection.pending.stop[0].resolve();
  await stopped;
  await tick();
  connection.pending.start[1].resolve();
  await second;

  assert.deepEqual(connection.events, [
    "start begins",
    "start fails",
    "stop begins",
    "stop ends",
    "start begins",
    "start ends",
  ]);
});

test("starts asked for during a start share it; stops during a stop share it", async () => {
  const connection = fakeConnection();
  const sequencer = createConnectionSequencer(connection);

  const a = sequencer.start();
  const b = sequencer.start();
  assert.equal(a, b);
  await tick();
  connection.pending.start[0].resolve();
  await a;

  const c = sequencer.stop();
  const d = sequencer.stop();
  assert.equal(c, d);
  await tick();
  connection.pending.stop[0].resolve();
  await c;

  assert.deepEqual(connection.events, ["start begins", "start ends", "stop begins", "stop ends"]);
});
