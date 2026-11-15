"use client";

import { useState } from "react";

/**
 * The IDs of items that arrived after the first load, so a panel can highlight them briefly (phone panel, POS log;
 * Design §18.7, §18.9). Worked out while rendering when the items change (React's pattern for state that follows other
 * data); an ID stays marked, so its CSS animation runs once. A status change on an item already shown doesn't mark it.
 */
export function useFreshIds(items: readonly { id: string }[] | undefined): ReadonlySet<string> {
  const [seen, setSeen] = useState<{
    items: readonly { id: string }[] | undefined;
    ids: Set<string> | null;
  }>({ items: undefined, ids: null });
  const [fresh, setFresh] = useState<ReadonlySet<string>>(new Set());

  if (items && items !== seen.items) {
    const ids = new Set(items.map((item) => item.id));
    if (seen.ids) {
      const arrived = items.filter((item) => !seen.ids!.has(item.id)).map((item) => item.id);
      if (arrived.length > 0) setFresh(new Set([...fresh, ...arrived]));
    }
    setSeen({ items, ids });
  }

  return fresh;
}
