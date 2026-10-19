import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { useLocation } from 'react-router';
import { getLocale, type Locale } from '@/lib/i18n';
import { baseOptions } from '@/lib/layout.shared';
import { button } from '@/lib/ui';

export function meta() {
  return [
    { title: 'Not Found | EasyEcs' },
    { name: 'robots', content: 'noindex' },
  ];
}

const copy: Record<
  Locale,
  { title: string; text: string; home: string; docs: string }
> = {
  en: {
    title: 'Page not found',
    text: 'This page may have moved or been removed.',
    home: 'Home',
    docs: 'Read the docs',
  },
  zh: {
    title: '页面不存在',
    text: '这个页面可能已被移动或删除。',
    home: '首页',
    docs: '阅读文档',
  },
};

// Plain links: build/client/404.html is served without JavaScript.
function Message({ lang }: { lang: Locale }) {
  const t = copy[lang];
  return (
    <div className="flex flex-col items-center gap-3">
      <h1 className="text-2xl font-medium tracking-tight">{t.title}</h1>
      <p className="text-fd-muted-foreground">{t.text}</p>
      <div className="flex flex-wrap justify-center gap-3">
        <a href={`/${lang}`} className={button('secondary')}>
          {t.home}
        </a>
        <a href={`/${lang}/docs`} className={button()}>
          {t.docs}
        </a>
      </div>
    </div>
  );
}

export default function NotFound() {
  const { pathname } = useLocation();
  // The prerendered /404 page is shown for every language, so it says both.
  const langs: Locale[] = pathname.startsWith('/404')
    ? ['en', 'zh']
    : [getLocale(pathname.split('/')[1])];

  return (
    <HomeLayout {...baseOptions(langs[0])}>
      <main className="mx-auto flex w-full max-w-xl flex-1 flex-col items-center justify-center gap-10 p-8 text-center">
        <p className="font-mono text-sm text-fd-muted-foreground">404</p>
        {langs.map((lang) => (
          <Message key={lang} lang={lang} />
        ))}
      </main>
    </HomeLayout>
  );
}
