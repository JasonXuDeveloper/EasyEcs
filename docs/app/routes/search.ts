import { createFromSource } from 'fumadocs-core/search/server';
import { source } from '@/lib/source';

// Prerendered to /api/search.json at build time; the search dialog downloads
// it and searches in the browser, so no search server or API key is needed.
const server = createFromSource(source, {
  localeMap: {
    en: { language: 'english' },
    // Intl.Segmenter based word splitting, which handles Chinese
    zh: { language: 'multilingual' },
  },
  buildIndex(page) {
    return {
      id: page.url,
      url: page.url,
      title: page.data.title ?? '',
      description: page.data.description,
      structuredData: page.data.structuredData,
    };
  },
});

export async function loader() {
  return server.staticGET();
}
