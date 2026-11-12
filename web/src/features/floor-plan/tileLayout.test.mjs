// Run with `npm test` (node --test). The tile text must stay inside every table of the seeded plan (Design §18.6).
import assert from "node:assert/strict";
import { test } from "node:test";
import { estimateWidth, layoutTile, tileInset } from "./tileLayout.mjs";

const smallRound = { shape: "round", width: 60, height: 60 };
const square = { shape: "rect", width: 90, height: 90 };
const turned = { shape: "rect", width: 90, height: 90, rotation: 90 };
const long = { shape: "rect", width: 140, height: 90 };
const bigRound = { shape: "round", width: 140, height: 140 };
const tables = [smallRound, square, turned, long, bigRound];

const lines = ({ status, guests, count, time, guest }) => [
  { key: "name", kind: "name", variants: [{ text: "T10" }] },
  {
    key: "status",
    kind: "text",
    bold: true,
    variants: [{ text: status }, { text: status, scale: 0.85 }],
  },
  {
    key: "party",
    kind: "text",
    variants: [
      { text: `${guests} · ${time}` },
      { text: `${count} · ${time}`, icon: true },
      { text: guests },
      { text: String(count), icon: true },
    ],
  },
  { key: "guest", kind: "text", truncate: guest },
];

const english = lines({
  status: "Arriving",
  guests: "8 guests",
  count: 8,
  time: "18:25",
  guest: "Alexandra Johnson",
});
const chinese = lines({
  status: "即将到店",
  guests: "8 位",
  count: 8,
  time: "18:25",
  guest: "欧阳娜娜",
});

/** Every placed line's box (estimated width × line height) lies inside the inset outline. */
const assertInside = (table, placed) => {
  const w = (table.rotation ?? 0) % 180 === 90 ? table.height : table.width;
  const h = (table.rotation ?? 0) % 180 === 90 ? table.width : table.height;
  const a = w / 2 - tileInset;
  const b = h / 2 - tileInset;
  for (const line of placed) {
    const half = line.width / 2;
    for (const dy of [line.y - line.size / 2, line.y + line.size / 2]) {
      assert.ok(
        Math.abs(dy) <= b,
        `${line.key} "${line.text}" crosses the top or bottom of a ${table.shape} ${w}×${h}`,
      );
      const room = table.shape === "round" ? a * Math.sqrt(1 - (dy / b) ** 2) : a;
      assert.ok(
        half <= room + 1e-9,
        `${line.key} "${line.text}" crosses the side of a ${table.shape} ${w}×${h}`,
      );
    }
  }
};

test("every line stays inside every seeded table size, in English and Chinese", () => {
  for (const table of tables) {
    for (const tile of [english, chinese]) {
      const placed = layoutTile(table, tile);
      assert.deepEqual(
        placed.slice(0, 2).map((l) => l.key),
        ["name", "status"],
      );
      assertInside(table, placed);
    }
  }
});

test("the smallest table keeps four lines and drops the time before the party size", () => {
  const placed = layoutTile(smallRound, english);
  assert.deepEqual(
    placed.map((l) => l.key),
    ["name", "status", "party", "guest"],
  );
  const party = placed.find((l) => l.key === "party");
  assert.ok(!party.text.includes("18:25"), party.text);
  assert.match(party.text, /8/);
});

test("a large table shows the whole party line and the whole name", () => {
  const placed = layoutTile(bigRound, english);
  assert.equal(placed.find((l) => l.key === "party").text, "8 guests · 18:25");
  assert.equal(placed.find((l) => l.key === "guest").text, "Alexandra Johnson");
});

test("a medium table uses the person icon before it drops the time", () => {
  const party = layoutTile(square, english).find((l) => l.key === "party");
  assert.equal(party.text, "8 · 18:25");
  assert.equal(party.icon, true);
});

test("a long name is cut by characters with an ellipsis", () => {
  const guest = layoutTile(square, english).find((l) => l.key === "guest");
  assert.match(guest.text, /^Alex.*…$/);
  const zh = layoutTile(smallRound, chinese).find((l) => l.key === "guest");
  assert.ok(zh === undefined || /^欧.*…$|^欧阳娜娜$/.test(zh.text), zh?.text);
});

test("Chinese and the ellipsis count as one em", () => {
  assert.equal(estimateWidth("欧阳…", 10), 30);
});

test("a suggested table's status fits on every table, in English and Chinese", () => {
  for (const table of tables) {
    for (const [tile, word] of [
      [english, "Suggested"],
      [chinese, "建议"],
    ]) {
      const status = {
        key: "status",
        kind: "text",
        bold: true,
        variants: [
          { text: `★ ${word}` },
          { text: word },
          { text: word, scale: 0.8 },
          { text: "★" },
        ],
      };
      const placed = layoutTile(table, [tile[0], status, ...tile.slice(2)]);
      assert.ok(placed[1].text.includes(word), `${table.shape} ${table.width}: ${placed[1].text}`);
      assertInside(table, placed);
    }
  }
});
