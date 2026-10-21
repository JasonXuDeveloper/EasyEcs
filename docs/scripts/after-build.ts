/**
 * The static files and the SEO checks, run inside `react-router build` by the
 * `afterBuild` plugin (vite.config.ts): this file is an entry of the server
 * build, so it shares the compiled pages (src/lib/source) with the prerender
 * instead of compiling every page a third time.
 *
 * - `beforePrerender`: the social cards need only the pages' data, so sharp
 *   draws them on its own threads while the prerender renders on this one.
 * - `afterPrerender`: the files made from the prerendered pages, then the
 *   checks over the finished build/client (they need the cards).
 */
import { drawCards, generateStatic } from './generate-static';
import { lintSeo } from './lint-seo';

let cards: Promise<void> | undefined;

export function beforePrerender() {
  cards = drawCards();
  // Awaited after the prerender; a failure before that is not unhandled.
  cards.catch(() => {});
}

export async function afterPrerender() {
  await generateStatic();
  await (cards ?? drawCards());
  lintSeo();
}
