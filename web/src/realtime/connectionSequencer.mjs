// Start / stop sequencing for the host and presenter screens' SignalR connection (M11b, Design §17.3). Plain
// JavaScript with JSDoc types and no imports, so `npm test` (node --test) can test it directly.
//
// The SignalR client logs "Failed to start the HttpConnection before stop() was called." as an error when stop() lands
// while a start is still in flight (seen on WebKit, whose start is slower: the socket dropped, a start began, then the
// browser's `offline` event stopped it). So every start and stop goes through here, one after the other:
// - stop() never reaches the connection while a start is in flight: it waits for that start to settle first;
// - start() never reaches the connection while a stop is in flight: it waits for that stop first;
// - a start asked for while one is already in flight (and no stop is waiting) shares it.

/**
 * @typedef {{ start: () => Promise<void>, stop: () => Promise<void> }} StartStop
 * @typedef {{ start: () => Promise<void>, stop: () => Promise<void>, isStarting: () => boolean, isStopping: () => boolean }} ConnectionSequencer
 */

/** @param {Promise<unknown>} promise @returns {Promise<void>} resolves when `promise` settles, never rejects */
const settled = (promise) =>
  promise.then(
    () => undefined,
    () => undefined,
  );

/**
 * @param {StartStop} connection the SignalR connection (or anything with the same start / stop)
 * @returns {ConnectionSequencer}
 */
export function createConnectionSequencer(connection) {
  /** @type {Promise<void> | null} */
  let starting = null;
  /** @type {Promise<void> | null} */
  let stopping = null;

  /**
   * Remembers `run` in a slot until it settles, unless a newer run took the slot meanwhile.
   * @param {Promise<void>} run
   * @param {(value: Promise<void> | null) => void} set
   * @param {() => Promise<void> | null} get
   */
  const track = (run, set, get) => {
    set(run);
    const clear = () => {
      if (get() === run) set(null);
    };
    run.then(clear, clear);
    return run;
  };

  return {
    start() {
      if (starting && !stopping) return starting;
      const pendingStop = stopping;
      const run = (async () => {
        if (pendingStop) await settled(pendingStop);
        await connection.start();
      })();
      return track(
        run,
        (value) => (starting = value),
        () => starting,
      );
    },
    stop() {
      if (stopping) return stopping;
      const pendingStart = starting;
      const run = (async () => {
        if (pendingStart) await settled(pendingStart);
        await connection.stop();
      })();
      return track(
        run,
        (value) => (stopping = value),
        () => stopping,
      );
    },
    isStarting: () => starting !== null,
    isStopping: () => stopping !== null,
  };
}
