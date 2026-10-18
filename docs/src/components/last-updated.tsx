import type { Locale } from '@/lib/i18n';

// Formatted in UTC so the prerendered HTML and the hydrated page agree.
const formats: Record<Locale, Intl.DateTimeFormat> = {
  en: new Intl.DateTimeFormat('en-US', { dateStyle: 'long', timeZone: 'UTC' }),
  zh: new Intl.DateTimeFormat('zh-CN', { dateStyle: 'long', timeZone: 'UTC' }),
};

const labels: Record<Locale, string> = {
  en: 'Last updated on',
  zh: '最后更新于',
};

/**
 * "Last updated on October 16, 2026" / 「最后更新于 2026年10月16日」, from the
 * page's git history (fumadocs-mdx `lastModified`). Renders nothing when the
 * date is unknown. Fumadocs' own PageLastUpdate formats the date in an effect,
 * in the browser's locale, so it would be missing from the prerendered HTML.
 */
export function LastUpdated({ date, lang }: { date?: Date; lang: Locale }) {
  if (!date || Number.isNaN(date.getTime())) return null;
  return (
    <p className="text-sm text-fd-muted-foreground">
      {labels[lang]}{' '}
      <time dateTime={date.toISOString()}>{formats[lang].format(date)}</time>
    </p>
  );
}
