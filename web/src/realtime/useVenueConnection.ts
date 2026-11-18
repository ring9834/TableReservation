"use client";

import { HubConnectionBuilder, HubConnectionState, type HubConnection } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { getGetFloorPlanQueryKey } from "@/api/endpoints/floor-plan/floor-plan";
import { getGetTonightBookingsQueryKey } from "@/api/endpoints/bookings/bookings";
import { getGetMessagesQueryKey } from "@/api/endpoints/messages/messages";
import { getGetPosMessagesQueryKey } from "@/api/endpoints/pos/pos";
import { getGetWaitlistQueryKey } from "@/api/endpoints/waitlist/waitlist";
import type { MessageLogChangedNotice, WaitlistChangedNotice } from "@/api/model";
import { apiBaseUrl } from "@/lib/apiFetch";
import { createConnectionSequencer } from "./connectionSequencer.mjs";
import { hubUrl } from "./hubUrl.mjs";
import { createSignalRLogger, isUnauthorized } from "./signalrLogger.mjs";

/**
 * - `connecting`: first connection, nothing missed yet.
 * - `connected`: notices arrive.
 * - `reconnecting`: the connection was lost (or the browser is offline); the screen may be behind (AC2.5).
 */
export type ConnectionStatus = "connecting" | "connected" | "reconnecting";

/**
 * Retry quickly at first, then every 5 s, and never give up while the API or the network is away: a host screen stays
 * open all evening. A 401 is different: the session is missing or has ended, so retrying can't help (M11a).
 */
const retryDelays = [0, 1_000, 2_000, 5_000];

/** What a screen wants to hear about besides the query invalidation the hook always does. */
export type VenueConnectionOptions = Readonly<{
  /** The host screen highlights a suggested party for a freed table (AC4.2, Design §18.6). */
  onWaitlistChanged?: (notice: WaitlistChangedNotice) => void;
  /** The presenter's phone panel (Design §18.7). */
  onMessageLogChanged?: (notice: MessageLogChangedNotice) => void;
}>;

/**
 * The host screen's SignalR connection to `/hubs/venue` (Design §15, §18.5). The demo session cookie is the only
 * credential; the server picks the venue from it, so the client never names one.
 *
 * - `floorPlanChanged` invalidates the floor plan, tonight's bookings and the waitlist (a table taken by someone else
 *   ends its suggestion); the hooks refetch (ADR-002).
 * - `waitlistChanged` invalidates the waitlist and is passed to `onWaitlistChanged`, which highlights a suggested
 *   party for a freed table (AC4.2, Design §18.6).
 * - `messageLogChanged` invalidates the message log (the presenter's phone panel) and tonight's bookings (the host's
 *   reminder line, M10b), and is passed to `onMessageLogChanged` (Design §18.7, §18.10).
 * - `demoReset` invalidates every query (Design §15, §18.10).
 * - `posLogChanged` invalidates the POS log under every filter (the presenter's POS panel, Design §18.9).
 * - After any reconnect every query is invalidated once, so nothing missed while away stays missing (AC2.5).
 * - A 401 from negotiation, on the first start or while reconnecting (the session ended), stops retrying and calls
 *   `onSignedOut`, which goes to sign-in; nothing is logged (M11a).
 * - `demoClockChanged` invalidates every query, as `demoReset` does: business time moved (M11a, Design §17.2).
 * - The browser's `offline` event stops the connection and shows "reconnecting" at once; `online` starts it
 *   again. An open WebSocket can outlive a lost network for a while, and the browser knows sooner.
 * - Reconnecting is ours, not the client's automatic reconnect: a lost connection closes, and `onclose` schedules the
 *   next start with the delays above. Starts and stops never overlap (`connectionSequencer.mjs`, M11b): a stop
 *   (offline, unmount; React Strict Mode mounts every effect twice in development) waits for a start in flight to
 *   settle, and a start waits for a stop in flight, so the client never logs a start abandoned by stop(). Failed
 *   starts while the API or network is down are one console.warn per outage (`signalrLogger.mjs`); the banner is the
 *   user-facing signal.
 */
export function useVenueConnection(
  venueId: string,
  onSignedOut: () => void,
  options: VenueConnectionOptions = {},
): ConnectionStatus {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  // Read through a ref, so a new callback never restarts the connection.
  const optionsRef = useRef(options);
  useEffect(() => {
    optionsRef.current = options;
  }, [options]);

  useEffect(() => {
    let disposed = false;
    let offline = typeof navigator !== "undefined" && navigator.onLine === false;
    let hasConnected = false;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    // No automatic reconnect: its starts are internal and can't be awaited, so a stop() while one was in flight (the
    // socket dropped, then the browser's `offline` event) made the client log "Failed to start the HttpConnection
    // before stop() was called." Every start is ours instead (`onclose` schedules the next one), and the sequencer
    // runs starts and stops one at a time: a stop waits for a start in flight, a start for a stop (M11b, §17.3).
    const logger = createSignalRLogger(() => disposed || sequencer.isStopping());
    const connection: HubConnection = new HubConnectionBuilder()
      // Absolute, never relative: a relative URL crashes the client in the production bundle (hubUrl.mjs).
      .withUrl(hubUrl(apiBaseUrl, "/hubs/venue", window.location.origin), { withCredentials: true })
      .configureLogging(logger)
      .build();
    const sequencer = createConnectionSequencer(connection);

    const stop = () => void sequencer.stop();

    const catchUp = () => void queryClient.invalidateQueries();

    connection.on("floorPlanChanged", () => {
      void queryClient.invalidateQueries({ queryKey: getGetFloorPlanQueryKey(venueId) });
      void queryClient.invalidateQueries({ queryKey: getGetTonightBookingsQueryKey(venueId) });
      void queryClient.invalidateQueries({ queryKey: getGetWaitlistQueryKey(venueId) });
    });

    connection.on("waitlistChanged", (notice: WaitlistChangedNotice) => {
      void queryClient.invalidateQueries({ queryKey: getGetWaitlistQueryKey(venueId) });
      optionsRef.current.onWaitlistChanged?.(notice);
    });

    connection.on("messageLogChanged", (notice: MessageLogChangedNotice) => {
      void queryClient.invalidateQueries({ queryKey: getGetMessagesQueryKey(venueId) });
      // A reminder was sent: tonight's list carries it (M10b, Design §18.10).
      void queryClient.invalidateQueries({ queryKey: getGetTonightBookingsQueryKey(venueId) });
      optionsRef.current.onMessageLogChanged?.(notice);
    });

    // The prefix key, so the POS log refetches under every filter (Design §18.9).
    connection.on("posLogChanged", () => {
      void queryClient.invalidateQueries({ queryKey: getGetPosMessagesQueryKey(venueId) });
    });

    // The presenter reset the demo (M10b): everything may have changed.
    connection.on("demoReset", catchUp);
    // The presenter moved the demo clock (M11a): overdue flags, arriving soon and "now" all changed.
    connection.on("demoClockChanged", catchUp);

    // A close is the connection lost (the API or the network went away), our own stop() (offline or unmount), or the
    // first transport failing after negotiating: start again, after the retry delay, unless we meant it or a start is
    // already on its way.
    connection.onclose(() => {
      if (disposed || offline || sequencer.isStopping() || sequencer.isStarting()) return;
      setStatus("reconnecting");
      scheduleStart();
    });

    const start = async () => {
      if (disposed) return;
      // Already starting or connected, unless a stop is queued (offline, then online again before it ran): then a new
      // start has to follow that stop.
      const alreadyUp = sequencer.isStarting() || connection.state === HubConnectionState.Connected;
      if (alreadyUp && !sequencer.isStopping()) return;
      if (offline) {
        setStatus("reconnecting"); // opened while offline: wait for the "online" event
        return;
      }
      try {
        await sequencer.start(); // after a stop still in flight, if any
        if (disposed || offline) return;
        logger.connected();
        attempt = 0;
        setStatus("connected");
        if (hasConnected) catchUp(); // after any reconnect: fetch whatever was missed (AC2.5)
        hasConnected = true;
      } catch (error) {
        if (disposed || offline) return; // we are stopping it ourselves: expected, nothing to show
        if (isUnauthorized(error)) {
          onSignedOut(); // the session is missing or has ended: retrying can't help (M11a)
          return;
        }
        if (hasConnected || attempt > 0) setStatus("reconnecting");
        scheduleStart();
      }
    };

    const scheduleStart = () => {
      clearTimeout(retryTimer);
      const delay = retryDelays[Math.min(attempt, retryDelays.length - 1)] || 1_000;
      attempt += 1;
      retryTimer = setTimeout(() => void start(), delay);
    };

    const handleOffline = () => {
      offline = true;
      clearTimeout(retryTimer);
      setStatus("reconnecting");
      stop();
    };
    const handleOnline = () => {
      offline = false;
      attempt = 0;
      hasConnected = true; // whatever happened while offline must be fetched again
      void start();
    };

    window.addEventListener("offline", handleOffline);
    window.addEventListener("online", handleOnline);
    void start();

    return () => {
      disposed = true;
      clearTimeout(retryTimer);
      window.removeEventListener("offline", handleOffline);
      window.removeEventListener("online", handleOnline);
      stop();
    };
  }, [queryClient, venueId, onSignedOut]);

  return status;
}
