import { createFromSource } from 'fumadocs-core/search/server';
import { source } from '@/lib/source';
import type { Route } from './+types/search';

// Prerendered to /api/search/{lang}.json at build time; the search dialog
// downloads its language's index and searches in the browser, so no search
// server or API key is needed (and a reader downloads one language, not both).
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

let exported: ReturnType<typeof server.export> | undefined;

export async function loader({ params }: Route.LoaderArgs) {
  const lang = params.file?.replace(/\.json$/, '');
  exported ??= server.export();
  const { data } = (await exported) as { data: Record<string, unknown> };
  if (!lang || !(lang in data))
    throw new Response('Not found', { status: 404 });
  // The i18n format of Fumadocs' static search, with this language only.
  return Response.json({ type: 'i18n', data: { [lang]: data[lang] } });
}
