// The host screen's "Full screen" button (M12b, Design §17.5). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it with stand-in objects; TypeScript reads the types through `allowJs`.
// iPad Safari before 16.4 has only the webkit-prefixed API; iPhone Safari has neither for a page (video only).

/**
 * @typedef {{
 *   fullscreenEnabled?: boolean,
 *   webkitFullscreenEnabled?: boolean,
 *   fullscreenElement?: Element | null,
 *   webkitFullscreenElement?: Element | null,
 *   exitFullscreen?: () => Promise<void>,
 *   webkitExitFullscreen?: () => void,
 * }} FullscreenDocument
 * @typedef {{
 *   matchMedia: (query: string) => { matches: boolean },
 *   navigator: object,
 * }} FullscreenWindow
 * @typedef {{
 *   requestFullscreen?: () => Promise<void>,
 *   webkitRequestFullscreen?: () => void,
 * }} FullscreenTarget
 */

/** Matches while the host app runs installed (M11c): it is already full screen. */
export const standaloneQuery = "(display-mode: standalone)";

/** The events that say full screen started or ended, with WebKit's prefixed one. */
export const fullscreenEvents = ["fullscreenchange", "webkitfullscreenchange"];

/**
 * Whether to offer the button: the browser can put the page full screen, and it isn't the installed app.
 * @param {FullscreenDocument} doc
 * @param {FullscreenWindow} win
 * @returns {boolean}
 */
export function canOfferFullscreen(doc, win) {
  if (
    win.matchMedia(standaloneQuery).matches ||
    /** @type {{ standalone?: boolean }} */ (win.navigator).standalone === true
  )
    return false;
  return !!(doc.fullscreenEnabled || doc.webkitFullscreenEnabled);
}

/**
 * @param {FullscreenDocument} doc
 * @returns {boolean}
 */
export function isFullscreen(doc) {
  return !!(doc.fullscreenElement ?? doc.webkitFullscreenElement);
}

/**
 * Puts `target` (the whole page: `document.documentElement`) full screen.
 * @param {FullscreenTarget} target
 * @returns {Promise<void>}
 */
export function enterFullscreen(target) {
  if (target.requestFullscreen) return target.requestFullscreen();
  if (target.webkitRequestFullscreen) {
    target.webkitRequestFullscreen();
    return Promise.resolve();
  }
  return Promise.reject(new Error("Full screen is not supported."));
}

/**
 * @param {FullscreenDocument} doc
 * @returns {Promise<void>}
 */
export function exitFullscreen(doc) {
  if (doc.exitFullscreen) return doc.exitFullscreen();
  if (doc.webkitExitFullscreen) {
    doc.webkitExitFullscreen();
    return Promise.resolve();
  }
  return Promise.resolve();
}
