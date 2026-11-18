// The absolute URL of a SignalR hub (Design §15). Plain JavaScript with JSDoc types and no imports, so `npm test`
// (node --test) can test it directly; TypeScript reads the types through `allowJs`.
//
// The SignalR client takes a relative URL down a path (HttpConnection._resolveUrl → logger.log) that, in the
// production bundle, reads a binding before it is initialised and crashes the host and presenter screens at render
// ("Cannot access '…' before initialization"). An absolute URL returns early. In AWS the API base is empty (one
// origin behind CloudFront), so the page's own origin is used, read in the browser when the connection is built, never
// at module load (the static export is prerendered without a window).

/**
 * The hub's absolute URL: the API base when one is set, else the page's origin.
 * @param {string} apiBaseUrl The API base without a trailing slash; "" means the page's own origin.
 * @param {string} path The hub path, e.g. "/hubs/venue".
 * @param {string} origin The page's origin (`window.location.origin`), used when the base is empty.
 * @returns {string}
 */
export function hubUrl(apiBaseUrl, path, origin) {
  const base = apiBaseUrl === "" ? origin : apiBaseUrl;
  return new URL(path, `${base}/`).toString();
}
