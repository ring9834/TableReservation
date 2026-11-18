// Run with `npm test` (node --test). The hub URL is always absolute (Design §15).
import assert from "node:assert/strict";
import { test } from "node:test";
import { hubUrl } from "./hubUrl.mjs";

const origin = "https://d1234abcd.cloudfront.net";

test("an empty API base (one origin, AWS) uses the page's origin", () => {
  assert.equal(hubUrl("", "/hubs/venue", origin), "https://d1234abcd.cloudfront.net/hubs/venue");
});

test("a set API base (local, CI) is used as it is, whatever the page's origin", () => {
  assert.equal(
    hubUrl("https://localhost:5001", "/hubs/venue", "https://localhost:3000"),
    "https://localhost:5001/hubs/venue",
  );
});

test("the URL is absolute either way", () => {
  for (const base of ["", "https://localhost:5001"]) {
    assert.match(hubUrl(base, "/hubs/venue", origin), /^https:\/\//);
  }
});
