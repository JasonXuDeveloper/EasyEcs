import { docs } from 'collections/server';
import { loader } from 'fumadocs-core/source';
import { lucideIconsPlugin } from 'fumadocs-core/source/plugins/lucide-icons';
import { i18n } from './i18n';

// content/README.md is the docs index for readers on GitHub; the site's
// index is index.mdx, so README.md is left out. (Filtered here: the
// collection's `files` globs cannot exclude it, Fumadocs prefixes each
// pattern with "./" and the build's import.meta.glob has no extglobs.)
const files = docs.toFumadocsSource();
files.files = files.files.filter((file) => file.path !== 'README.md');

// Pages are served at /{lang}/docs/{page}; content/index.mdx is /{lang}/docs.
export const source = loader({
  baseUrl: '/docs',
  source: files,
  i18n,
  plugins: [lucideIconsPlugin()],
});

/** Frontmatter fields added in source.config.ts (`docSchema`) */
export interface DocExtras {
  seoTitle?: string;
  faq?: boolean;
}

export function docExtras(page: { data: object }): DocExtras {
  return page.data as DocExtras;
}

type Page = NonNullable<ReturnType<typeof source.getPage>>;

/**
 * True when a page is shown in a language it has no file for: a Chinese URL
 * whose content is the English page (i18n fallback, src/lib/i18n.ts).
 */
export function isFallback(page: Page, lang: string) {
  if (lang === 'en') return false;
  return (
    !page.path.endsWith(`.${lang}.mdx`) && !page.path.endsWith(`.${lang}.md`)
  );
}

/** The page in a language, only when it has its own file in that language */
export function getOwnPage(slugs: string[], lang: string) {
  const page = source.getPage(slugs, lang);
  return page && !isFallback(page, lang) ? page : undefined;
}
