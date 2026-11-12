// Which table a host screen pre-selects (Design §18.6). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.

/**
 * The free fitting tables in the API's order (smallest first, floor order; Design §11.5): the suggestion, then the
 * alternatives.
 * @param {{ table: { id: string } | null, alternatives: { id: string }[] } | undefined} suggestion
 * @returns {string[]}
 */
export const freeTableIds = (suggestion) =>
  suggestion ? [suggestion.table, ...suggestion.alternatives].filter(Boolean).map((t) => t.id) : [];

/**
 * The table to pre-select: the first preferred table that is still free (a waiting party's notified table, then the
 * table freed for it), otherwise the API's suggestion, otherwise none.
 * @param {(string | null | undefined)[]} preferred
 * @param {string[]} free
 * @returns {string | null}
 */
export const defaultTableId = (preferred, free) =>
  preferred.find((id) => !!id && free.includes(id)) ?? free[0] ?? null;

/**
 * The entry to highlight for a freed table (AC4.2): the API's answer to `GET …/waitlist?tableId=` once it has arrived
 * for that table, the notice's suggestion until then.
 * @param {{ tableId: string, entryId: string | null } | null} freed
 * @param {{ suggestedEntryId: string | null } | undefined} waitlist the answer for the current query key
 * @param {boolean} answered true when `waitlist` is the answer for `freed.tableId` (not placeholder data)
 * @returns {string | null}
 */
export const suggestedEntryId = (freed, waitlist, answered) => {
  if (!freed) return null;
  return answered ? (waitlist?.suggestedEntryId ?? null) : freed.entryId;
};
