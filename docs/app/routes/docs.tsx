import browserCollections from 'collections/browser';
import type * as PageTree from 'fumadocs-core/page-tree';
import { useFumadocsLoader } from 'fumadocs-core/source/client';
import { Callout } from 'fumadocs-ui/components/callout';
import { DocsLayout } from 'fumadocs-ui/layouts/docs';
import {
  DocsBody,
  DocsDescription,
  DocsPage,
  DocsTitle,
  EditOnGitHub,
} from 'fumadocs-ui/layouts/docs/page';
import { Link } from 'react-router';
import { LLMCopyButton, ViewOptions } from '@/components/ai/page-actions';
import { handlePageFeedback } from '@/components/feedback/actions';
import { Feedback } from '@/components/feedback/client';
import { LastUpdated } from '@/components/last-updated';
import { firstCommitDate } from '@/lib/git-dates.server';
import { getLocale, type Locale } from '@/lib/i18n';
import { baseOptions } from '@/lib/layout.shared';
import { faqFromMarkdown, pageBodyMarkdown } from '@/lib/markdown';
import { type DocSeo, docMeta, markdownPath } from '@/lib/seo';
import { contentBlobUrl, contentEditUrl } from '@/lib/site';
import { docExtras, getOwnPage, isFallback, source } from '@/lib/source';
import { getMDXComponents } from '@/mdx-components';
import type { Route } from './+types/docs';

export async function loader({ params }: Route.LoaderArgs) {
  const lang = getLocale(params.lang);
  const slugs = params['*']?.split('/').filter(Boolean) ?? [];
  const page = source.getPage(slugs, lang);
  if (!page) throw new Response('Not found', { status: 404 });

  // A page without a file in this language shows the English page.
  const fallback = isFallback(page, lang);
  const englishUrl = fallback
    ? (getOwnPage(page.slugs, 'en')?.url ?? `/en/docs/${slugs.join('/')}`)
    : undefined;

  // From the docs home to this page, for the BreadcrumbList.
  const breadcrumbs = [
    { name: 'EasyEcs', url: `/${lang}` },
    {
      name: lang === 'zh' ? 'EasyEcs 文档' : 'EasyEcs Docs',
      url: `/${lang}/docs`,
    },
  ];
  // A folder's index page is named after its folder in the sidebar.
  const folderNames = new Map<string, string>();
  const collect = (nodes: PageTree.Node[]) => {
    for (const node of nodes) {
      if (node.type !== 'folder') continue;
      // $id is "<lang>:<folder path>", e.g. "en:guide"
      const folder = node.$id?.slice(node.$id.indexOf(':') + 1);
      if (folder && typeof node.name === 'string')
        folderNames.set(folder, node.name);
      collect(node.children);
    }
  };
  collect(source.getPageTree(lang).children);
  for (let i = 1; i <= slugs.length; i++) {
    const parentSlugs = slugs.slice(0, i);
    const parent = source.getPage(parentSlugs, lang);
    if (parent)
      breadcrumbs.push({
        name: folderNames.get(parentSlugs.join('/')) ?? parent.data.title,
        url: parent.url,
      });
  }

  const lastModified = page.data.lastModified;
  const own = (l: Locale) => getOwnPage(page.slugs, l)?.url;
  const seo: DocSeo = {
    lang,
    indexable: !fallback,
    url: page.url,
    canonicalUrl: englishUrl,
    title: page.data.title,
    seoTitle: docExtras(page).seoTitle,
    description: page.data.description,
    lastModified:
      lastModified && !Number.isNaN(lastModified.getTime())
        ? lastModified.toISOString()
        : undefined,
    published: await firstCommitDate(`content/${page.path}`),
    alternates: { en: own('en'), zh: own('zh') },
    breadcrumbs,
    faq: docExtras(page).faq
      ? faqFromMarkdown(await pageBodyMarkdown(page, lang))
      : undefined,
  };

  return {
    lang,
    englishUrl,
    path: page.path,
    // The Markdown copy of a fallback page is the English one.
    markdownUrl: markdownPath(englishUrl ?? page.url),
    seo,
    pageTree: await serializedTree(source.getPageTree(lang)),
  };
}

/**
 * The sidebar of a language, serialized once for all its pages (the prerender
 * serializes it ~140 times otherwise); keyed by the tree, which a content
 * change in `dev` replaces.
 */
const serializedTrees = new WeakMap<
  PageTree.Root,
  ReturnType<typeof source.serializePageTree>
>();
function serializedTree(tree: PageTree.Root) {
  let serialized = serializedTrees.get(tree);
  if (!serialized) {
    serialized = source.serializePageTree(tree);
    serializedTrees.set(tree, serialized);
  }
  return serialized;
}

export function meta({ loaderData }: Route.MetaArgs) {
  if (!loaderData) return [];
  return docMeta(loaderData.seo);
}

interface ContentProps {
  lang: Locale;
  path: string;
  markdownUrl: string;
  englishUrl?: string;
}

/** On a Chinese URL whose page has no Chinese file yet */
function FallbackNotice({ englishUrl }: { englishUrl: string }) {
  return (
    <Callout type="info" title="本页暂无中文版">
      下面是英文原文，中文版正在编写中。
      <Link to={englishUrl} className="font-medium underline">
        打开英文页面
      </Link>
    </Callout>
  );
}

const clientLoader = browserCollections.docs.createClientLoader<ContentProps>({
  component(
    { default: Mdx, frontmatter, toc, lastModified },
    { lang, path, markdownUrl, englishUrl },
  ) {
    // <title>, description, canonical, social cards and JSON-LD come from
    // `meta` above (src/lib/seo.ts).
    return (
      <DocsPage toc={toc} full={frontmatter.full}>
        <DocsTitle>{frontmatter.title}</DocsTitle>
        <DocsDescription className="mb-0">
          {frontmatter.description}
        </DocsDescription>
        <div className="mb-4 flex flex-row items-center gap-2 border-b pt-2 pb-4">
          <LLMCopyButton markdownUrl={markdownUrl} locale={lang} />
          <ViewOptions
            markdownUrl={markdownUrl}
            githubUrl={`${contentBlobUrl}/${path}`}
            locale={lang}
          />
        </div>
        {englishUrl && <FallbackNotice englishUrl={englishUrl} />}
        <DocsBody>
          <Mdx components={getMDXComponents()} />
        </DocsBody>
        <div className="flex flex-row flex-wrap items-center justify-between gap-4">
          <EditOnGitHub href={`${contentEditUrl}/${path}`} />
          <LastUpdated date={lastModified} lang={lang} />
        </div>
        <Feedback onSendAction={handlePageFeedback} />
      </DocsPage>
    );
  },
});

export default function DocsRoute({ loaderData }: Route.ComponentProps) {
  const { pageTree, lang, path, markdownUrl, englishUrl } =
    useFumadocsLoader(loaderData);

  return (
    <DocsLayout tree={pageTree} {...baseOptions(lang)}>
      {clientLoader.useContent(path, { lang, path, markdownUrl, englishUrl })}
    </DocsLayout>
  );
}
