import type { StaticOptions } from 'fumadocs-core/search/client';

/**
 * How much a match in a page's title outweighs a match in a heading or a
 * paragraph. Each record of the index (a page title, a heading, a paragraph,
 * a table row) is one document with one `content` field, so ZBSearch's
 * per-field `boost` cannot tell a title from body text; the custom `sortBy`
 * of its search parameters can. 3 is the smallest factor at which every
 * query of scripts/search-expectations.json finds its page (`bun run lint:search`).
 */
const titleBoost = 3;

/**
 * API reference pages keep their plain score: their titles are type names
 * ("Prefab", "RelationIndex<T>"), which would otherwise outrank the guide
 * page about the feature ("Prefabs and cloning"). Searching for a type name
 * still finds its page, since the title then matches exactly.
 */
const reference = /\/docs\/reference\//;

/** A record of the index: Fumadocs' `type` ('page' for a title) and `url` */
function weight(record: Record<string, unknown>) {
  return record.type === 'page' && !reference.test(String(record.url))
    ? titleBoost
    : 1;
}

/**
 * The search parameters of the search dialog (src/components/search.tsx) and
 * of scripts/lint-search.ts, which checks the ranking: BM25 scores as
 * ZBSearch computes them, page titles weighted by `titleBoost`. Fumadocs
 * groups the results by page in the order of each page's best record, so a
 * page whose title matches comes first.
 */
export const searchParams = {
  sortBy: ([, scoreA, a], [, scoreB, b]) =>
    scoreB * weight(b) - scoreA * weight(a),
} satisfies StaticOptions['search'];
