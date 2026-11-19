"use client";

import { useCallback, useSyncExternalStore } from "react";
import {
  canOfferFullscreen,
  enterFullscreen,
  exitFullscreen,
  fullscreenEvents,
  isFullscreen,
  standaloneQuery,
} from "./fullscreen.mjs";

const subscribeFullscreen = (onChange: () => void) => {
  for (const name of fullscreenEvents) document.addEventListener(name, onChange);
  return () => {
    for (const name of fullscreenEvents) document.removeEventListener(name, onChange);
  };
};

const subscribeSupport = (onChange: () => void) => {
  const standalone = window.matchMedia(standaloneQuery);
  standalone.addEventListener("change", onChange);
  return () => standalone.removeEventListener("change", onChange);
};

const notInBrowser = () => false;

/**
 * The host's "Full screen" button (M12b, Design §17.5): whether to offer it (the browser supports it and the page isn't
 * the installed app) and whether the page is full screen now. Both follow the browser's events, so leaving with Escape
 * turns "Exit full screen" back into "Full screen". Prerendering (no window) offers nothing.
 */
export function useFullscreen() {
  const supported = useSyncExternalStore(
    subscribeSupport,
    () => canOfferFullscreen(document, window),
    notInBrowser,
  );
  const active = useSyncExternalStore(
    subscribeFullscreen,
    () => isFullscreen(document),
    notInBrowser,
  );

  const toggle = useCallback(() => {
    const change = isFullscreen(document)
      ? exitFullscreen(document)
      : enterFullscreen(document.documentElement);
    // A request the browser refuses (no user gesture, a policy) leaves the page as it was; nothing to tell the host.
    change.catch(() => {});
  }, []);

  return { supported, active, toggle };
}
