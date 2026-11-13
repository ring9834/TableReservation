import { expect, request } from "@playwright/test";
import path from "node:path";

/**
 * The host session signed in once by global-setup.ts and shared by every spec file (Design §18.5): /demo-login allows
 * 5 attempts a minute per IP, so the specs never sign in through the form themselves. The file is git-ignored.
 */
export const hostSessionPath = path.join(__dirname, "..", ".auth", "host.json");

/** The presenter session, signed in once by global-setup.ts the same way (Design §18.7). Git-ignored too. */
export const presenterSessionPath = path.join(__dirname, "..", ".auth", "presenter.json");

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

type ApiContext = Awaited<ReturnType<typeof request.newContext>>;

/** Runs `work` with an API client signed in as the shared host session. */
const withHostApi = async <T>(work: (api: ApiContext) => Promise<T>): Promise<T> => {
  const api = await request.newContext({
    baseURL: apiBaseUrl,
    storageState: hostSessionPath,
    ignoreHTTPSErrors: true,
  });
  try {
    return await work(api);
  } finally {
    await api.dispose();
  }
};

const venuePath = `/api/v1/venues/${venueId}`;

type SeatedListItem = HostListItem & { tables: { id: string; name: string }[] };
type WaitlistEntryItem = { id: string; name: string; partySize: number; version: number };
type FloorTable = { id: string; name: string; seats: number; current: { bookingId: string } | null };

/**
 * Clears the tables of the given seated bookings (POST …/tables/{id}/clear {bookingId, version}, Design §14.2), so
 * the walk-ins a spec seated don't stay on the floor. Bookings already completed are left alone.
 */
export const clearBookings = async (references: string[]) => {
  if (references.length === 0) return;
  await withHostApi(async (api) => {
    const list = await api.get(`${venuePath}/bookings`);
    expect(list.ok(), `host list answered ${list.status()}`).toBe(true);
    const { items } = (await list.json()) as { items: SeatedListItem[] };
    for (const item of items.filter((i) => references.includes(i.reference) && i.status === "seated")) {
      const clear = await api.post(`${venuePath}/tables/${item.tables[0].id}/clear`, {
        data: { bookingId: item.id, version: item.version },
      });
      expect(clear.ok(), `clear ${item.reference} answered ${clear.status()}`).toBe(true);
    }
  });
};

/** Ends active waitlist entries as `removed` (POST …/waitlist/{id}/end, Design §14.2): by ID, or every one `match`es. */
export const endWaitlistEntries = async (match: string[] | ((entry: WaitlistEntryItem) => boolean)) => {
  await withHostApi(async (api) => {
    const list = await api.get(`${venuePath}/waitlist`);
    expect(list.ok(), `waitlist answered ${list.status()}`).toBe(true);
    const { items } = (await list.json()) as { items: WaitlistEntryItem[] };
    const chosen = items.filter((entry) => (Array.isArray(match) ? match.includes(entry.id) : match(entry)));
    for (const entry of chosen) {
      const end = await api.post(`${venuePath}/waitlist/${entry.id}/end`, {
        data: { outcome: "removed", version: entry.version },
      });
      expect(end.ok(), `end waitlist entry answered ${end.status()}`).toBe(true);
    }
  });
};

/**
 * Makes sure a party is seated at `tableName`, as the seed has it at 18:25 (Design §17). A previous run (or a person
 * trying the demo) may have cleared it; then a stand-in walk-in of `partySize` is seated there through the API, so
 * step 3 starts from the seeded shape. Returns the stand-in's reference, or null when the table was already taken.
 */
export const ensureSeated = async (tableName: string, partySize: number) =>
  withHostApi(async (api) => {
    const plan = await api.get(`${venuePath}/floor-plan`);
    expect(plan.ok(), `floor plan answered ${plan.status()}`).toBe(true);
    const { tables } = (await plan.json()) as { tables: FloorTable[] };
    const table = tables.find((t) => t.name === tableName);
    expect(table, `table ${tableName} exists`).toBeTruthy();
    if (table!.current) return null;
    const seat = await api.post(`${venuePath}/walk-ins`, {
      data: { partySize, tableId: table!.id, name: "E2E stand-in" },
    });
    expect(seat.ok(), `stand-in walk-in on ${tableName} answered ${seat.status()}`).toBe(true);
    return ((await seat.json()) as { reference: string }).reference;
  });

type TableSuggestion = { table: { id: string; name: string; seats: number } | null };

/**
 * Seats an "E2E stand-in" walk-in of `partySize` on every table the API still suggests for that party now
 * (GET …/tables/suggestion, then POST …/walk-ins), until it suggests none, so "no table fits" holds whatever was
 * cleared by hand since the last reset. Returns the stand-ins' references for the clean-up. Stops after `limit`
 * seatings in case the API keeps suggesting a table.
 */
export const seatStandInsUntilNoTable = async (partySize: number, limit = 20) =>
  withHostApi(async (api) => {
    const references: string[] = [];
    for (let i = 0; i < limit; i++) {
      const answer = await api.get(`${venuePath}/tables/suggestion?partySize=${partySize}`);
      expect(answer.ok(), `table suggestion answered ${answer.status()}`).toBe(true);
      const { table } = (await answer.json()) as TableSuggestion;
      if (!table) return references;
      const seat = await api.post(`${venuePath}/walk-ins`, {
        data: { partySize, tableId: table.id, name: "E2E stand-in" },
      });
      expect(seat.ok(), `stand-in walk-in on ${table.name} answered ${seat.status()}`).toBe(true);
      references.push(((await seat.json()) as { reference: string }).reference);
    }
    throw new Error(`the API still suggested a table for ${partySize} after ${limit} stand-ins`);
  });

/** The quoted wait the API gives a party now (GET …/waitlist/quote, BR-08, Design §11.5). */
export const quotedWait = async (partySize: number) =>
  withHostApi(async (api) => {
    const quote = await api.get(`${venuePath}/waitlist/quote?partySize=${partySize}`);
    expect(quote.ok(), `quote answered ${quote.status()}`).toBe(true);
    return ((await quote.json()) as { quotedWaitMinutes: number }).quotedWaitMinutes;
  });
