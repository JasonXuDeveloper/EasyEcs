import { defineI18n } from 'fumadocs-core/i18n';

// Pages use the dot parser: `page.mdx` is English, `page.zh.mdx` is Chinese.
// A page without a Chinese file falls back to English in the Chinese tree
// (fallbackLanguage defaults to the default language): it is served at
// /zh/docs/... with a notice, `noindex`, and a canonical link to the English
// page (app/routes/docs.tsx, src/lib/seo.ts).
export const i18n = defineI18n({
  defaultLanguage: 'en',
  languages: ['en', 'zh'],
});

export const locales = [
  { locale: 'en', name: 'English' },
  { locale: 'zh', name: '简体中文' },
];

export type Locale = 'en' | 'zh';

export function getLocale(lang?: string): Locale {
  return lang === 'zh' ? 'zh' : 'en';
}
