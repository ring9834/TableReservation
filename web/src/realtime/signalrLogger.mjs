// The SignalR client's logger for the host screen (Design §18.5). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.
//
// The SignalR client logs every failed start and reconnect attempt with console.error, and the Next.js dev overlay
// shows each one. Those failures are expected (the API restarting, the network gone, or our own stop() during
// start-up, which React Strict Mode does on every mount in development), and the "reconnecting" banner is the signal
// for the user. So:
// - a failure caused by our own stop() is silent;
// - a connection failure (API or network down) is one console.warn per outage;
// - anything else at Error or above stays a console.error.

/** `@microsoft/signalr` LogLevel values (a numeric enum), copied so this file needs no imports. */
export const SignalRLogLevel = Object.freeze({
  Trace: 0,
  Debug: 1,
  Information: 2,
  Warning: 3,
  Error: 4,
  Critical: 5,
  None: 6,
});

/** Messages the client logs when we stopped the connection ourselves while it was starting. */
const stoppedByUs = [
  "stopped during negotiation",
  "stopped before the hub handshake could complete",
  "connection was stopped",
  "AbortError",
];

/** Messages the client logs when the API or the network cannot be reached. */
const connectionFailures = [
  "Failed to start the connection",
  "Failed to complete negotiation with the server",
  "Failed to start the transport",
  "Unable to connect to the server",
  "Connection disconnected with error",
  "Unexpected response code",
  "Error from HTTP request",
  "Timeout from HTTP request",
  "WebSocket failed to connect",
];

/**
 * How a message should be reported: `"silent"`, `"outage"` (a connection failure, warned once per outage), `"warn"`
 * or `"error"`.
 * @param {number} level SignalR log level
 * @param {string} message
 * @param {boolean} stopping true while our own stop() is in progress (unmount, Strict Mode remount, offline)
 * @returns {"silent" | "outage" | "warn" | "error"}
 */
export function classifySignalRLog(level, message, stopping) {
  if (level < SignalRLogLevel.Warning) return "silent";
  if (stopping && stoppedByUs.some((text) => message.includes(text))) return "silent";
  if (connectionFailures.some((text) => message.includes(text))) return "outage";
  return level >= SignalRLogLevel.Error ? "error" : "warn";
}

/**
 * @typedef {{ warn: (...data: unknown[]) => void, error: (...data: unknown[]) => void }} LogSink
 * @typedef {{ log: (level: number, message: string) => void, connected: () => void }} HostSignalRLogger
 */

/**
 * A SignalR `ILogger`. Connection failures are warned once until {@link HostSignalRLogger.connected} is called again.
 * @param {() => boolean} isStopping
 * @param {LogSink} [sink]
 * @returns {HostSignalRLogger}
 */
export function createSignalRLogger(isStopping, sink = console) {
  let warnedThisOutage = false;
  return {
    log(level, message) {
      const kind = classifySignalRLog(level, message, isStopping());
      if (kind === "error") {
        sink.error(`[SignalR] ${message}`);
      } else if (kind === "warn") {
        sink.warn(`[SignalR] ${message}`);
      } else if (kind === "outage" && !warnedThisOutage) {
        warnedThisOutage = true;
        sink.warn(`[SignalR] ${message} (retrying; the screen shows "reconnecting")`);
      }
    },
    connected() {
      warnedThisOutage = false;
    },
  };
}
