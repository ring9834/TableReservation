// "Scan to book" on the presenter screen (M12b, Design §17.5). Plain JavaScript with JSDoc types and no imports, so
// `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.

/** Modules of light margin around the code on every side (the QR standard's quiet zone). */
export const quietZone = 4;

/**
 * The guest booking page on this site: the page's own origin plus `/book/`. Read in the browser, so the code follows
 * whatever address the presenter opened (localhost, or the CloudFront address in AWS). Only ever the guest page:
 * any path or query on the origin is dropped, so no staff screen can end up in the code.
 * @param {string} origin The page's origin (`window.location.origin`).
 * @returns {string}
 */
export function bookingUrl(origin) {
  return new URL("/book/", origin).toString();
}

/**
 * One SVG path that draws every dark module as a 1 × 1 square, row by row.
 * @param {number} size Modules per side.
 * @param {(row: number, col: number) => boolean} isDark
 * @returns {string}
 */
export function qrPath(size, isDark) {
  const parts = [];
  for (let row = 0; row < size; row++) {
    for (let col = 0; col < size; col++) {
      if (isDark(row, col)) parts.push(`M${col} ${row}h1v1h-1z`);
    }
  }
  return parts.join("");
}

/**
 * The SVG viewBox for a code of `size` modules, with the quiet zone inside it.
 * @param {number} size Modules per side.
 * @returns {string}
 */
export function viewBoxFor(size) {
  return `${-quietZone} ${-quietZone} ${size + 2 * quietZone} ${size + 2 * quietZone}`;
}
