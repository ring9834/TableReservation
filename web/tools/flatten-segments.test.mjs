import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { flattenSegments } from "./flatten-segments.mjs";

/** A temporary out/ shaped like Next 16's static export. */
const exportFolder = () => {
  const out = mkdtempSync(join(tmpdir(), "flatten-segments-"));
  const files = {
    "__next._tree.txt": "root tree",
    "__next.__PAGE__.txt": "root page",
    "book/__next._tree.txt": "book tree",
    "book/__next.book/__PAGE__.txt": "book page",
    "host/__next.host/__PAGE__.txt": "host page",
    "a/b/__next.a/b/__PAGE__.txt": "nested page",
    "a/b/__next.a/b/layout.txt": "nested layout",
    "_next/static/chunks/app.js": "js",
  };
  for (const [path, text] of Object.entries(files)) {
    mkdirSync(dirname(join(out, path)), { recursive: true });
    writeFileSync(join(out, path), text);
  }

  return out;
};

test("each file in a __next.<seg> folder gets its dotted sibling, nested paths joined with dots", () => {
  const out = exportFolder();

  const written = flattenSegments(out);

  assert.deepEqual(written, [
    "a/b/__next.a.b.__PAGE__.txt",
    "a/b/__next.a.b.layout.txt",
    "book/__next.book.__PAGE__.txt",
    "host/__next.host.__PAGE__.txt",
  ]);
  assert.equal(readFileSync(join(out, "book/__next.book.__PAGE__.txt"), "utf8"), "book page");
  assert.equal(readFileSync(join(out, "a/b/__next.a.b.layout.txt"), "utf8"), "nested layout");
});

test("the originals stay and files outside __next.<seg> folders are untouched", () => {
  const out = exportFolder();

  flattenSegments(out);

  assert.ok(existsSync(join(out, "book/__next.book/__PAGE__.txt")));
  assert.equal(readFileSync(join(out, "__next.__PAGE__.txt"), "utf8"), "root page");
  assert.ok(!existsSync(join(out, "_next/static.chunks.app.js")));
});

test("running it again writes nothing", () => {
  const out = exportFolder();
  flattenSegments(out);

  assert.deepEqual(flattenSegments(out), []);
  assert.equal(readFileSync(join(out, "host/__next.host.__PAGE__.txt"), "utf8"), "host page");
});

test("a stale dotted copy is replaced by the current file", () => {
  const out = exportFolder();
  writeFileSync(join(out, "book/__next.book.__PAGE__.txt"), "old");

  assert.ok(flattenSegments(out).includes("book/__next.book.__PAGE__.txt"));
  assert.equal(readFileSync(join(out, "book/__next.book.__PAGE__.txt"), "utf8"), "book page");
});
