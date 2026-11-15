// Links in the phone panel's texts (M10b, Design §7.3, §18.10). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it directly. The stored body is never changed; only its display is.

/** Every link action the API sends (`LinkAction`); each has a button label under `phone.actions`. */
export const actionKinds = /** @type {const} */ (["confirm", "cancel", "manage"]);

const urlPattern = /https?:\/\/[^\s]+/g;

/** How many characters of the token stay visible before the ellipsis. */
const visibleTokenChars = 4;

/**
 * The text split into plain parts and links, in order, so links can be shown short and still be links.
 * @param {string} body
 * @returns {({ kind: "text", text: string } | { kind: "link", url: string, label: string })[]}
 */
export const segments = (body) => {
  /** @type {({ kind: "text", text: string } | { kind: "link", url: string, label: string })[]} */
  const parts = [];
  let last = 0;
  for (const match of body.matchAll(urlPattern)) {
    const start = match.index ?? 0;
    if (start > last) parts.push({ kind: "text", text: body.slice(last, start) });
    parts.push({ kind: "link", url: match[0], label: shortLink(match[0]) });
    last = start + match[0].length;
  }
  if (last < body.length) parts.push({ kind: "text", text: body.slice(last) });
  return parts;
};

/**
 * A long link shown short: host and path, then the first characters of the token and an ellipsis, e.g.
 * `localhost:3000/manage/?token=AbC1…`. Anything that isn't a URL with a token is shortened after 40 characters.
 * @param {string} url
 */
export const shortLink = (url) => {
  try {
    const parsed = new URL(url);
    const token = parsed.searchParams.get("token");
    const base = `${parsed.host}${parsed.pathname}`;
    if (token) return `${base}?token=${token.slice(0, visibleTokenChars)}…`;
    return base.length > 40 ? `${base.slice(0, 40)}…` : base;
  } catch {
    return url.length > 40 ? `${url.slice(0, 40)}…` : url;
  }
};

/**
 * The manage page for a guest link, on this site's own origin (`/manage/?token=…`), so the phone's frame is always
 * same-origin even when the link's base URL differs. `null` for anything that isn't a manage link with a token.
 * @param {string} url
 */
export const managePath = (url) => {
  try {
    const parsed = new URL(url);
    const token = parsed.searchParams.get("token");
    if (!token || !/\/manage\/?$/.test(parsed.pathname)) return null;
    return `/manage/?token=${encodeURIComponent(token)}`;
  } catch {
    return null;
  }
};
