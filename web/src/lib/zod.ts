// The only way the app imports zod (ESLint no-restricted-imports; the generated src/api/zod files are rewritten to
// import it from here by orval's afterAllFilesWrite hook, tools/use-zod-wrapper.mjs).
//
// zod 4 compiles a fast path for object schemas with `new Function`, and probes for it with a caught
// `new Function("")` the first time a z.object is built. Under the site's CSP (no 'unsafe-eval', Design §17.4) the
// probe is refused and reported as a violation on every page, even though zod recovers. `jitless` skips the probe and
// the compiled path; forms parse asynchronously (zodResolver), which never used it anyway. The setting must be in
// place before the first schema is built, so every module that builds one imports zod through this file.
import { z } from "zod";

z.config({ jitless: true });

export * from "zod";
export { z };
