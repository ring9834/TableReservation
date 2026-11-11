import { useEffect, useRef } from "react";

/** Moves keyboard and screen-reader focus to a step's heading when the step appears (not on first page load). */
export function useFocusHeading<T extends HTMLElement>(enabled: boolean) {
  const ref = useRef<T>(null);
  useEffect(() => {
    if (enabled) {
      ref.current?.focus();
    }
  }, [enabled]);
  return ref;
}
