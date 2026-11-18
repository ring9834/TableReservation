// Segment prefetch files for S3 / CloudFront (M12a, Design §17.4). Runs after `next build`, before csp-hashes.mjs.
//
// The static export writes each route's segment prefetch files into a folder, e.g. out/book/__next.book/__PAGE__.txt,
// but the client requests the dotted name, out/book/__next.book.__PAGE__.txt. `next dev` answers either; S3 doesn't,
// so every prefetch fails (403 through origin access control). This step copies each file under a directory named
// __next.<seg> to the dotted sibling: its path inside that directory joined with dots, so a nested
// __next.a/b/__PAGE__.txt becomes __next.a.b.__PAGE__.txt. The originals stay. Running it again changes nothing.
//
// Usage: node tools/flatten-segments.mjs [outDir]

import { copyFileSync, existsSync, readdirSync, readFileSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const SEGMENT_DIR = /^__next\..+/;

/** Every file under a directory, as paths relative to it. */
function filesUnder(dir, base = dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? filesUnder(path, base) : [relative(base, path)];
  });
}

/** The __next.<seg> directories under outDir (not those inside another one, which the outer one already covers). */
function segmentDirs(dir) {
  return readdirSync(dir, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .flatMap((entry) => {
      const path = join(dir, entry.name);
      return SEGMENT_DIR.test(entry.name) ? [path] : segmentDirs(path);
    });
}

/**
 * Copies every file under each __next.<seg> directory to its dotted sibling. Returns the dotted files written (paths
 * relative to outDir); a dotted file that already has the same content is left alone and not listed.
 */
export function flattenSegments(outDir) {
  const written = [];
  for (const segmentDir of segmentDirs(outDir)) {
    for (const file of filesUnder(segmentDir)) {
      const source = join(segmentDir, file);
      const target = `${segmentDir}.${file.split(sep).join(".")}`;
      if (existsSync(target) && readFileSync(target).equals(readFileSync(source))) {
        continue;
      }

      copyFileSync(source, target);
      written.push(relative(outDir, target).split(sep).join("/"));
    }
  }

  return written.sort();
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const outDir = process.argv[2] ?? "out";
  const written = flattenSegments(outDir);
  console.log(`Segment prefetch files: ${written.length} dotted copies written.`);
}
