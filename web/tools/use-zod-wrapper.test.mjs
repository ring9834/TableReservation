import { test } from "node:test";
import assert from "node:assert/strict";
import { pointAtZodWrapper } from "./use-zod-wrapper.mjs";

test("a direct zod import is pointed at the wrapper", () => {
  assert.equal(
    pointAtZodWrapper("import * as zod from 'zod';\nexport const A = zod.object({});\n"),
    "import * as zod from '@/lib/zod';\nexport const A = zod.object({});\n",
  );
  assert.equal(
    pointAtZodWrapper('import * as zod from "zod";'),
    "import * as zod from '@/lib/zod';",
  );
});

test("other imports are left alone and a second run changes nothing", () => {
  const source = "import * as zod from 'zod/mini';\nimport { x } from 'zodiac';\n";
  assert.equal(pointAtZodWrapper(source), source);
  const once = pointAtZodWrapper("import * as zod from 'zod';");
  assert.equal(pointAtZodWrapper(once), once);
});
