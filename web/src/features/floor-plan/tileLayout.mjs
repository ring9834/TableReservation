// Fits a table tile's text inside the table's outline (Design §18.6). Plain JavaScript with JSDoc types and no
// imports, so `npm test` (node --test) can test it directly; TypeScript reads the types through `allowJs`.
//
// Widths are estimated per character, on the generous side (CJK and "…" one em, Latin letters 0.6–0.95 em), so the
// real text is never wider than the estimate. Everything is in floor units: the SVG scales as a whole, so a label
// that fits in floor units fits at every screen width.

/** Space kept between the text and the outline: half the selected outline's stroke plus a little air. */
export const tileInset = 5;

/** Tables smaller than this (in their shorter side) use the small type sizes. */
const smallTable = 80;

/** Space between the person icon and the number, in em. */
const iconWidthEm = 0.9;

const isWide = (char) =>
  /[\u2600-\u27BF\u2E80-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF\u3000-\u303F\u2026]/.test(char);

/**
 * The estimated width of one character, in em.
 * @param {string} char
 */
const charEm = (char) => {
  if (isWide(char)) return 1;
  if (/[mwMW@]/.test(char)) return 0.95;
  if (/[A-Z]/.test(char)) return 0.7;
  if (/[ilj.,:;'|!]/.test(char)) return 0.4;
  if (char === " ") return 0.35;
  if (char === "\u00B7") return 0.6; // the middle dot, wide in some CJK fonts
  return 0.57; // lower-case letters, digits and the rest
};

/**
 * The estimated width of a text, in floor units.
 * @param {string} text
 * @param {number} size font size in floor units
 * @param {boolean} [bold]
 */
export const estimateWidth = (text, size, bold = false) =>
  Array.from(text).reduce((sum, char) => sum + charEm(char), 0) * size * (bold ? 1.1 : 1);

/**
 * Type sizes for a table: smaller type on the two-seat tables, so four lines still fit.
 * @param {number} shorterSide
 */
export const tileSizes = (shorterSide) =>
  shorterSide < smallTable
    ? { name: 11, text: 9, lineHeight: 10.5 }
    : { name: 13, text: 11, lineHeight: 13 };

/**
 * The width free for a line whose band runs from `top` to `bottom` (relative to the centre).
 * @param {{ shape: string, halfWidth: number, halfHeight: number }} box the inner box, inset applied
 * @param {number} top
 * @param {number} bottom
 */
const bandWidth = (box, top, bottom) => {
  const far = Math.max(Math.abs(top), Math.abs(bottom));
  if (far >= box.halfHeight) return 0;
  if (box.shape !== "round") return 2 * box.halfWidth;
  return 2 * box.halfWidth * Math.sqrt(1 - (far / box.halfHeight) ** 2);
};

/**
 * Cuts a text by characters (not bytes) with "…" until it fits, keeping at least one character.
 * @param {string} text
 * @param {(candidate: string) => boolean} fits
 * @returns {string | null}
 */
const truncateToFit = (text, fits) => {
  const chars = Array.from(text.trim());
  if (fits(chars.join(""))) return chars.join("");
  for (let keep = chars.length - 1; keep >= 1; keep--) {
    const candidate = `${chars.slice(0, keep).join("").trimEnd()}…`;
    if (fits(candidate)) return candidate;
  }
  return null;
};

/**
 * @typedef {{ text: string, icon?: boolean, scale?: number }} Variant one way to write a line; `icon` puts a person
 *   icon before it, `scale` shrinks the type (the last resort for a status word)
 * @typedef {{ key: string, kind: "name" | "text", bold?: boolean, variants?: Variant[], truncate?: string }} TileLine
 *   a line to place: the first variant that fits, or `truncate` cut with "…"
 * @typedef {{ key: string, text: string, icon: boolean, size: number, bold: boolean, y: number, width: number }} PlacedLine
 *   `y` is the line's middle relative to the table's centre; `width` the estimated width, icon included
 */

/**
 * Places a tile's lines inside the table: top to bottom in the order given, centred, each in the first form that
 * fits the width at its height. When a line cannot fit at all, the last lines are dropped (the guest name first,
 * then the party line) and the rest placed again, which gives them more room. The first two lines (name, status)
 * are always kept; if even they do not fit alone, each shows its last form.
 * @param {{ shape: string, width: number, height: number, rotation?: number }} table
 * @param {TileLine[]} lines
 * @returns {PlacedLine[]}
 */
export const layoutTile = (table, lines) => {
  const turned = Math.abs(table.rotation ?? 0) % 180 === 90;
  const width = turned ? table.height : table.width;
  const height = turned ? table.width : table.height;
  const box = {
    shape: table.shape,
    halfWidth: width / 2 - tileInset,
    halfHeight: height / 2 - tileInset,
  };
  const sizes = tileSizes(Math.min(width, height));

  /** @param {TileLine[]} chosen */
  const place = (chosen) => {
    const total = chosen.length * sizes.lineHeight;
    if (total > 2 * box.halfHeight) return null;
    /** @type {PlacedLine[]} */
    const placed = [];
    for (const [index, line] of chosen.entries()) {
      const base = line.kind === "name" ? sizes.name : sizes.text;
      const bold = line.bold ?? line.kind === "name";
      const top = -total / 2 + index * sizes.lineHeight;
      const room = bandWidth(box, top, top + sizes.lineHeight);
      /** @param {Variant} variant */
      const sizeOf = (variant) => base * (variant.scale ?? 1);
      /** @param {Variant} variant */
      const widthOf = (variant) =>
        estimateWidth(variant.text, sizeOf(variant), bold) +
        (variant.icon ? iconWidthEm * sizeOf(variant) : 0);
      const variant =
        line.truncate !== undefined
          ? (() => {
              const text = truncateToFit(line.truncate, (t) => widthOf({ text: t }) <= room);
              return text === null ? undefined : { text };
            })()
          : (line.variants ?? []).find((v) => widthOf(v) <= room);
      if (!variant) {
        if (chosen.length <= 2) {
          // Name and status always stay; the last form is the shortest.
          const last = line.variants?.at(-1) ?? { text: line.truncate ?? "" };
          placed.push({
            key: line.key,
            text: last.text,
            icon: !!last.icon,
            size: sizeOf(last),
            bold,
            y: top + sizes.lineHeight / 2,
            width: widthOf(last),
          });
          continue;
        }
        return null;
      }
      placed.push({
        key: line.key,
        text: variant.text,
        icon: !!variant.icon,
        size: sizeOf(variant),
        bold,
        y: top + sizes.lineHeight / 2,
        width: widthOf(variant),
      });
    }
    return placed;
  };

  for (let count = lines.length; count >= Math.min(2, lines.length); count--) {
    const placed = place(lines.slice(0, count));
    if (placed) return placed;
  }
  return place(lines.slice(0, Math.min(2, lines.length))) ?? [];
};

/** The person icon's width, in em, for drawing it before a line. */
export const personIconEm = iconWidthEm;
