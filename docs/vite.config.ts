import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { reactRouter } from '@react-router/dev/vite';
import tailwindcss from '@tailwindcss/vite';
import { fumadocsMdx } from 'fumadocs-mdx/vite';
import { defineConfig } from 'vite';

const root = path.dirname(fileURLToPath(import.meta.url));

// `--mode scripts`: the scripts in scripts/ (run with vite-node) only load the content.
export default defineConfig(({ mode }) => ({
  plugins:
    mode === 'scripts'
      ? [fumadocsMdx()]
      : [fumadocsMdx(), tailwindcss(), reactRouter()],
  optimizeDeps:
    mode === 'scripts' ? { noDiscovery: true, include: [] } : undefined,
  resolve: {
    alias: {
      '@': path.resolve(root, 'src'),
      collections: path.resolve(root, '.source'),
    },
  },
}));
