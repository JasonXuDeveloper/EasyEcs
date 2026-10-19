import { readFileSync } from 'node:fs';
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

// `--mode scripts`: the scripts in scripts/ (run with vite-node) only load the content.
export default defineConfig(({ mode }) => ({
  plugins:
    mode === 'scripts'
      ? [fumadocsMdx()]
      : [fumadocsMdx(), serverOnlyPageExports(), tailwindcss(), reactRouter()],
  define: { __DOCS_VERSION__: JSON.stringify(docsVersion()) },
  optimizeDeps:
    mode === 'scripts' ? { noDiscovery: true, include: [] } : undefined,
  resolve: {
    alias: {
      '@': path.resolve(root, 'src'),
      collections: path.resolve(root, '.source'),
    },
  },
}));
