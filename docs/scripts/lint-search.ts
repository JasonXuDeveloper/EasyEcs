/**
 * Checks the site search the way a reader uses it: each query of
 * scripts/search-expectations.json runs through Fumadocs' static search
 * client (the one the search dialog uses: ZBSearch over the built
 * /api/search/{lang}.json, the dialog's parameters from src/lib/search.ts),
 * and the page it expects must be the first result, or within the first
 * `top` results where two pages are legitimately equal.
 *
 * Usage: `bun run lint:search` after `react-router build` (`bun run build`
 * runs it last). Prints the top three pages of every query.
 */
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { staticClient } from 'fumadocs-core/search/client/orama-static';
import { searchParams } from '../src/lib/search';
import expectations from './search-expectations.json';

const indexDir = path.resolve(process.argv[2] ?? 'build/client/api/search');

interface Expectation {
  query: string;
  page: string;
  top?: number;
}

const failures: string[] = [];
const rows: string[] = [];

for (const [lang, list] of Object.entries(
  expectations as Record<string, Expectation[]>,
)) {
  const client = staticClient({
    // Bun's fetch reads file: URLs, so the client loads the built index as
    // the browser loads /api/search/{lang}.json.
    from: pathToFileURL(path.join(indexDir, `${lang}.json`)).href,
    locale: lang,
    search: searchParams,
  });
  const prefix = new RegExp(`^/${lang}/docs/?`);
  for (const { query, page, top = 1 } of list) {
    const results = await client.search(query);
    const pages: string[] = [];
    for (const result of results)
      if (result.type === 'page')
        pages.push(result.url.replace(prefix, '') || 'index');
    const rank = pages.indexOf(page) + 1;
    rows.push(`| ${lang} | ${query} | ${pages.slice(0, 3).join(', ')} |`);
    if (rank === 0 || rank > top)
      failures.push(
        `${lang} "${query}": ${page} is ${rank === 0 ? 'not found' : `#${rank}`}, expected ${top === 1 ? 'first' : `in the top ${top}`}`,
      );
  }
}

console.log('| Language | Query | Top pages |\n| --- | --- | --- |');
console.log(rows.join('\n'));
if (failures.length > 0) {
  console.error(
    `\nSearch: ${failures.length} queries miss their page (scripts/search-expectations.json):\n  ${failures.join('\n  ')}`,
  );
  process.exit(1);
}
console.log(`\nSearch: ${rows.length} queries find their page`);
