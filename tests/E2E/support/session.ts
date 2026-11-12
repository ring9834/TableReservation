import { expect, request } from "@playwright/test";
import path from "node:path";

/**
 * The host session signed in once by global-setup.ts and shared by every spec file (Design §18.5): /demo-login allows
 * 5 attempts a minute per IP, so the specs never sign in through the form themselves. The file is git-ignored.
 */
export const hostSessionPath = path.join(__dirname, "..", ".auth", "host.json");

/** The API the web app calls (CI: http://localhost:5000), for the clean-up, and Golden Dragon's seeded ID. */
export const apiBaseUrl =
  process.env.E2E_API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "https://localhost:5001";
export const venueId = process.env.E2E_VENUE_ID ?? "0193a7d0-0000-7000-8000-000000000001";

type HostListItem = { id: string; reference: string; status: string; version: number };

/**
 * Cancels the given bookings with the shared host session (POST …/bookings/{id}/cancel {version}, Design §14.1).
 * `day` is the venue-local date the bookings are on (`YYYY-MM-DD`); omitted, the host list's default (today).
 * Cancelled bookings don't count for pacing and free their tables, so reruns against one database keep working.
 */
export const cancelBookings = async (references: string[], day?: string) => {
  if (references.length === 0) return;
  const api = await request.newContext({
    baseURL: apiBaseUrl,
    storageState: hostSessionPath,
    ignoreHTTPSErrors: true,
  });
  try {
    const list = await api.get(`/api/v1/venues/${venueId}/bookings${day ? `?day=${day}` : ""}`);
    expect(list.ok(), `host list answered ${list.status()}`).toBe(true);
    const { items } = (await list.json()) as { items: HostListItem[] };
    for (const item of items.filter((i) => references.includes(i.reference) && i.status !== "cancelled")) {
      const cancel = await api.post(`/api/v1/venues/${venueId}/bookings/${item.id}/cancel`, {
        data: { version: item.version },
      });
      expect(cancel.ok(), `cancel ${item.reference} answered ${cancel.status()}`).toBe(true);
    }
  } finally {
    await api.dispose();
  }
};
