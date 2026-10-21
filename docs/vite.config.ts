import { readdirSync, readFileSync } from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { reactRouter } from '@react-router/dev/vite';
import tailwindcss from '@tailwindcss/vite';
import { fumadocsMdx } from 'fumadocs-mdx/vite';
import { defineConfig, type Plugin } from 'vite';

const root = path.dirname(fileURLToPath(import.meta.url));

/**
 * A compiled page also exports its processed Markdown (`_markdown`, for the
 * .md copies and llms files) and its search records (`structuredData`, for
 * /api/search/{lang}.json). Both are read at build time only; in the browser bundle
 * they become unused locals, which the minifier drops (a quarter of a page
 * chunk).
 */
function serverOnlyPageExports(): Plugin {
  return {
    name: 'easyecs:server-only-page-exports',
    enforce: 'post',
    applyToEnvironment: (environment) => environment.name === 'client',
    transform(code, id) {
      if (!id.includes('collection=docs')) return;
      return code.replace(
        /^export (?:const|let) (_markdown|structuredData) = /gm,
        'const $1 = ',
      );
    },
  };
}

/**
 * The release the docs describe: the newest "## X.Y.Z" section of the changelog, which is written for the coming
 * release before it is cut (docs/CLAUDE.md), shown as major.minor.
 */
function docsVersion() {
  const changelog = readFileSync(
    path.join(root, 'content/changelog.mdx'),
    'utf8',
  );
  const match = /^## (\d+)\.(\d+)\.\d+/m.exec(changelog);
  if (!match)
    throw new Error('content/changelog.mdx has no "## X.Y.Z" section');
  return `${match[1]}.${match[2]}`;
}

/** The analyzer's rules (EECS002…), counted in the generator's source, so the site never states a stale count */
function analyzerRules() {
  const dir = path.join(root, '../src/EasyEcs.Generator');
  const ids = new Set<string>();
  for (const file of readdirSync(dir)) {
    if (!file.endsWith('.cs')) continue;
    const source = readFileSync(path.join(dir, file), 'utf8');
    for (const match of source.matchAll(/"(EECS\d{3})"/g)) ids.add(match[1]);
  }
  if (ids.size === 0)
    throw new Error('No analyzer rules found in src/EasyEcs.Generator');
  return ids.size;
}

// `--mode scripts`: the scripts in scripts/ (run with vite-node) only load the
// content, compiled without diagrams or highlighting (source.config.ts).
export default defineConfig(({ mode }) => {
  if (mode === 'scripts') process.env.DOCS_CONTENT_ONLY = '1';
  return {
    plugins:
      mode === 'scripts'
        ? [fumadocsMdx()]
        : [
            fumadocsMdx(),
            serverOnlyPageExports(),
            tailwindcss(),
            reactRouter(),
          ],
    define: {
      __DOCS_VERSION__: JSON.stringify(docsVersion()),
      __ANALYZER_RULES__: JSON.stringify(analyzerRules()),
    },
    optimizeDeps:
      mode === 'scripts' ? { noDiscovery: true, include: [] } : undefined,
    resolve: {
      alias: {
        '@': path.resolve(root, 'src'),
        collections: path.resolve(root, '.source'),
      },
    },
  };
});
