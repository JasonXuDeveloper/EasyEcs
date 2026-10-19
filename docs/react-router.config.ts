import { glob } from 'node:fs/promises';
import type { Config } from '@react-router/dev/config';

// content/guide/change-tracking.zh.mdx -> guide/change-tracking
function getDocSlug(file: string) {
  const parts = file.replace(/\\/g, '/').split('/');
  const stem = (parts.pop() ?? '').replace(/(\.zh)?\.mdx?$/, '');
  return (stem === 'index' ? parts : [...parts, stem]).join('/');
}

export default {
  // Fully static: every page is prerendered into build/client, no server.
  ssr: false,
  future: {
    v8_middleware: true,
    v8_splitRouteModules: true,
    v8_viteEnvironmentApi: true,
    v8_passThroughRequests: true,
    v8_trailingSlashAwareDataRequests: true,
  },
  async prerender({ getStaticPaths }) {
    const paths = new Set([
      '/',
      '/en',
      '/zh',
      '/404',
      '/api/search/en.json',
      '/api/search/zh.json',
      ...getStaticPaths(),
    ]);
    // Every page in both languages: a page without a Chinese file is served
    // in the Chinese tree too, in English (i18n fallback, src/lib/i18n.ts).
    for await (const file of glob('**/*.{md,mdx}', { cwd: 'content' })) {
      if (file === 'README.md') continue; // the GitHub index, not a site page
      const slug = getDocSlug(file);
      for (const lang of ['en', 'zh'])
        paths.add(slug ? `/${lang}/docs/${slug}` : `/${lang}/docs`);
    }
    return [...paths];
  },
} satisfies Config;
