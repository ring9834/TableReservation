// orval's afterAllFilesWrite hook for the zod output (orval.config.ts). orval always writes `import * as zod from 'zod'`
// and has no option for another path; the generated schemas must import zod through src/lib/zod.ts, which turns off
// zod's eval probe before any schema is built (CSP without 'unsafe-eval', Design §17.4). Idempotent.
//
// Usage: node tools/use-zod-wrapper.mjs <file or folder>...   (orval appends the files it wrote)

import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const DIRECT = /from (['"])zod\1;/g;
const WRAPPER = "from '@/lib/zod';";

/** The source with every direct `from 'zod'` import pointed at the wrapper. */
export function pointAtZodWrapper(source) {
  return source.replace(DIRECT, WRAPPER);
}

const zodFiles = (path) =>
  statSync(path).isDirectory()
    ? readdirSync(path).flatMap((name) => zodFiles(join(path, name)))
    : path.endsWith(".zod.ts")
      ? [path]
      : [];

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  let changed = 0;
  for (const file of process.argv.slice(2).flatMap(zodFiles)) {
    const source = readFileSync(file, "utf8");
    const rewritten = pointAtZodWrapper(source);
    if (rewritten !== source) {
      writeFileSync(file, rewritten);
      changed++;
    }
  }

  console.log(`zod imports pointed at @/lib/zod in ${changed} generated file(s).`);
}
