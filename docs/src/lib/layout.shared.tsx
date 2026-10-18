import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { BookOpen, Gauge } from 'lucide-react';
import type { Locale } from './i18n';
import { githubUrl } from './site';

export function baseOptions(lang: Locale): BaseLayoutProps {
  const zh = lang === 'zh';
  return {
    nav: {
      title: (
        <span className="flex items-center gap-2">
          <img
            src="/icon.svg"
            alt=""
            width={24}
            height={24}
            className="size-6"
          />
          <span className="font-semibold text-base">EasyEcs</span>
        </span>
      ),
      url: `/${lang}`,
    },
    links: [
      {
        text: zh ? '文档' : 'Docs',
        url: `/${lang}/docs`,
        icon: <BookOpen />,
        active: 'nested-url',
        on: 'nav',
      },
      {
        text: zh ? '性能' : 'Performance',
        url: `/${lang}/docs/performance`,
        icon: <Gauge />,
        on: 'nav',
      },
    ],
    githubUrl,
  };
}
