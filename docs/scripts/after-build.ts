/**
 * Runs after `react-router build` (`bun run build`): generate-static.ts, then
 * lint-seo.ts, in one process, so the pages are compiled once. Loading
 * src/lib/source compiles every MDX file, the slow part of both scripts.
 */
export {};

const start = performance.now();
console.log(
  '[after-build] Compiling the pages for the static files and checks...',
);
await import('../src/lib/source');
console.log(
  `[after-build] Pages compiled in ${((performance.now() - start) / 1000).toFixed(1)}s`,
);
await import('./generate-static');
await import('./lint-seo');
