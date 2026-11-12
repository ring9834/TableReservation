// Run with `npm test` (node --test). Which table and waiting party the host screens pre-select (Design §18.6).
import assert from "node:assert/strict";
import { test } from "node:test";
import { defaultTableId, freeTableIds, suggestedEntryId } from "./tableChoice.mjs";

const suggestion = { table: { id: "T1" }, alternatives: [{ id: "T2" }, { id: "W1" }] };

test("free tables are the suggestion, then the alternatives", () => {
  assert.deepEqual(freeTableIds(suggestion), ["T1", "T2", "W1"]);
  assert.deepEqual(freeTableIds({ table: null, alternatives: [] }), []);
  assert.deepEqual(freeTableIds(undefined), []);
});

test("the first preferred table that is still free wins", () => {
  assert.equal(defaultTableId(["W1", "T2"], ["T1", "T2", "W1"]), "W1");
  assert.equal(defaultTableId([null, "T2"], ["T1", "T2"]), "T2");
});

test("a preferred table that is no longer free falls back to the suggestion", () => {
  assert.equal(defaultTableId(["T4", undefined], ["T1", "T2"]), "T1");
  assert.equal(defaultTableId([], ["T1"]), "T1");
});

test("nothing free means nothing pre-selected", () => {
  assert.equal(defaultTableId(["T4"], []), null);
});

test("the highlighted entry follows the API once it has answered for the freed table", () => {
  const freed = { tableId: "T4", entryId: "e1" };
  assert.equal(suggestedEntryId(null, { suggestedEntryId: "e1" }, true), null);
  assert.equal(suggestedEntryId(freed, undefined, false), "e1");
  assert.equal(suggestedEntryId(freed, { suggestedEntryId: null }, false), "e1");
  assert.equal(suggestedEntryId(freed, { suggestedEntryId: "e2" }, true), "e2");
  assert.equal(suggestedEntryId(freed, { suggestedEntryId: null }, true), null);
});
