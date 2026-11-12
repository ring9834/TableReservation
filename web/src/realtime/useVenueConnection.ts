"use client";

import {
  HubConnectionBuilder,
  HubConnectionState,
  type HubConnection,
  type IRetryPolicy,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { getGetFloorPlanQueryKey } from "@/api/endpoints/floor-plan/floor-plan";
import { getGetTonightBookingsQueryKey } from "@/api/endpoints/bookings/bookings";
import { apiBaseUrl } from "@/lib/apiFetch";
import { createSignalRLogger } from "./signalrLogger.mjs";

/**
 * - `connecting`: first connection, nothing missed yet.
 * - `connected`: notices arrive.
 * - `reconnecting`: the connection was lost (or the browser is offline); the screen may be behind (AC2.5).
 */
export type ConnectionStatus = "connecting" | "connected" | "reconnecting";

/** Retry quickly at first, then every 5 s, and never give up: a host screen stays open all evening. */
const retryDelays = [0, 1_000, 2_000, 5_000];
const retryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
    retryDelays[previousRetryCount] ?? 5_000,
};

/** The negotiate request answered 401: the demo session is missing or has ended. */
const isUnauthorized = (error: unknown) =>
  error instanceof Error && /Status code '401'/.test(error.message);

/**
 * The host screen's SignalR connection to `/hubs/venue` (Design §15, §18.5). The demo session cookie is the only
 * credential; the server picks the venue from it, so the client never names one.
 *
 * - `floorPlanChanged` invalidates the floor plan and tonight's bookings; the hooks refetch (ADR-002).
 * - After any reconnect every query is invalidated once, so nothing missed while away stays missing (AC2.5).
 * - The browser's `offline` event stops the connection and shows "reconnecting" at once; `online` starts it
 *   again. An open WebSocket can outlive a lost network for a while, and the browser knows sooner.
 * - A start we stop ourselves (unmount; React Strict Mode mounts every effect twice in development) is expected:
 *   no console error, no state change. Failed starts and reconnects while the API or network is down are one
 *   console.warn per outage (`signalrLogger.mjs`); the banner is the user-facing signal.
 */
export function useVenueConnection(venueId: string, onSignedOut: () => void): ConnectionStatus {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<ConnectionStatus>("connecting");

  useEffect(() => {
    let disposed = false;
    let offline = typeof navigator !== "undefined" && navigator.onLine === false;
    let hasConnected = false;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    /** True from our own stop() until the next start: failures it causes are expected. */
    let stopping = false;

    const logger = createSignalRLogger(() => disposed || stopping);
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/venue`, { withCredentials: true })
      .withAutomaticReconnect(retryPolicy)
      .configureLogging(logger)
      .build();

    const stop = () => {
      stopping = true;
      void connection.stop();
    };

    const catchUp = () => void queryClient.invalidateQueries();

    connection.on("floorPlanChanged", () => {
      void queryClient.invalidateQueries({ queryKey: getGetFloorPlanQueryKey(venueId) });
      void queryClient.invalidateQueries({ queryKey: getGetTonightBookingsQueryKey(venueId) });
    });

    connection.onreconnecting(() => {
      if (!disposed) setStatus("reconnecting");
    });
    connection.onreconnected(() => {
      if (disposed) return;
      logger.connected();
      setStatus("connected");
      catchUp();
    });
    // Automatic reconnect never gives up, so a close means stop() was called (offline or unmount) or the
    // first transport failed after negotiating; start again unless we meant it.
    connection.onclose(() => {
      if (!disposed && !offline && !stopping) {
        setStatus("reconnecting");
        scheduleStart();
      }
    });

    const start = async () => {
      if (disposed || connection.state === HubConnectionState.Connected) return;
      if (offline) {
        setStatus("reconnecting"); // opened while offline: wait for the "online" event
        return;
      }
      if (connection.state !== HubConnectionState.Disconnected) {
        scheduleStart(); // still stopping or reconnecting: look again shortly
        return;
      }
      stopping = false;
      try {
        await connection.start();
        if (disposed) return;
        logger.connected();
        attempt = 0;
        setStatus("connected");
        if (hasConnected) catchUp();
        hasConnected = true;
      } catch (error) {
        if (disposed || stopping) return; // we stopped it ourselves: expected, nothing to show
        if (isUnauthorized(error)) {
          onSignedOut();
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
