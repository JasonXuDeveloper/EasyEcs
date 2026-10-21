/**
 * Runs after `react-router build`: rewrites the page files to the URLs the
 * site links to, and writes the files that are not pages (404.html, sitemap,
 * robots.txt, llms.txt, Markdown copies of each page, social cards).
 */
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import type * as PageTree from 'fumadocs-core/page-tree';
import { benchNumber, formatRatio, sourceLine } from '../src/lib/benchmarks';
import {
  featureComparison,
  featureCounts,
} from '../src/lib/feature-comparison';
import type { Locale } from '../src/lib/i18n';
import { pageBodyMarkdown } from '../src/lib/markdown';
import { docDescription, markdownPath } from '../src/lib/seo';
import {
  docsVersion,
  githubUrl,
  installCommand,
  languageTag,
  nugetUrl,
  productSummary,
  siteUrl,
} from '../src/lib/site';
import { getOwnPage, isFallback, source } from '../src/lib/source';
import { type Card, homeCards, writeCards } from './og';

const outDir = path.resolve('build/client');
const locales = ['en', 'zh'] as const satisfies Locale[];

type Page = ReturnType<typeof source.getPages>[number];

async function write(file: string, content: string | Buffer) {
  const target = path.join(outDir, file);
  await fs.mkdir(path.dirname(target), { recursive: true });
  await fs.writeFile(target, content);
}

const localeOf = (page: Page): Locale => (page.locale === 'zh' ? 'zh' : 'en');
/** A page with its own file in its language (not an English fallback in the zh tree) */
const isOwn = (page: Page) => !isFallback(page, localeOf(page));

function lastModifiedOf(page: Page) {
  const date = page.data.lastModified;
  return date && !Number.isNaN(date.getTime()) ? date : undefined;
}

const isoDate = (date: Date) => date.toISOString().slice(0, 10);

/* ------------------------------------------------------------------ */
/* Page files                                                          */
/* ------------------------------------------------------------------ */

/**
 * React Router writes /en/docs/guide/queries as en/docs/guide/queries/index.html,
 * which Cloudflare Pages serves at /en/docs/guide/queries/ and reaches from the
 * slash-less URL only through a 308 redirect. Every link, canonical URL and
 * sitemap entry uses the slash-less URL, so move each page to
 * en/docs/guide/queries.html: Pages then serves it at /en/docs/guide/queries
 * directly and redirects the trailing-slash and .html forms there.
 */
async function flattenPages(dir: string) {
  let moved = 0;
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === 'assets') continue;
    const child = path.join(dir, entry.name);
    moved += await flattenPages(child);
    const index = path.join(child, 'index.html');
    try {
      await fs.rename(index, `${child}.html`);
      moved++;
      // Remove the folder if the page was all it held.
      await fs.rmdir(child).catch(() => {});
    } catch {}
  }
  return moved;
}

/* ------------------------------------------------------------------ */
/* Markdown                                                            */
/* ------------------------------------------------------------------ */

const markdownCache = new Map<string, Promise<string>>();

/**
 * The Markdown copy of a page: title, description, canonical URL, version,
 * last update and the body with every component rendered to Markdown.
 */
function markdownOf(page: Page) {
  let cached = markdownCache.get(page.url);
  if (!cached) {
    cached = renderMarkdown(page);
    markdownCache.set(page.url, cached);
  }
  return cached;
}

async function renderMarkdown(page: Page) {
  const lang = localeOf(page);
  const zh = lang === 'zh';
  const date = lastModifiedOf(page);
  const other = getOwnPage(page.slugs, zh ? 'en' : 'zh');

  const lines = [`# ${page.data.title}`, ''];
  if (page.data.description) lines.push(`> ${page.data.description}`, '');
  lines.push(`URL: ${siteUrl}${page.url}`);
  lines.push(`${zh ? '版本' : 'Version'}: EasyEcs ${docsVersion}`);
  if (date) lines.push(`${zh ? '最后更新' : 'Last updated'}: ${isoDate(date)}`);
  if (other)
    lines.push(
      `${zh ? 'English' : '中文版'}: ${siteUrl}${markdownPath(other.url)}`,
    );
  lines.push('', await pageBodyMarkdown(page, lang), '');
  return lines.join('\n');
}

/* ------------------------------------------------------------------ */
/* Sitemap and robots.txt                                              */
/* ------------------------------------------------------------------ */

function xmlEscape(value: string) {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** hreflang alternates, the same set as the pages' <link rel="alternate"> */
function alternateLinks(urls: Partial<Record<Locale, string>>) {
  const links: [string, string][] = [];
  if (urls.en) links.push([languageTag.en, urls.en]);
  if (urls.zh) links.push(['zh', urls.zh], [languageTag.zh, urls.zh]);
  const fallback = urls.en ?? urls.zh;
  if (fallback) links.push(['x-default', fallback]);
  return links
    .map(
      ([lang, url]) =>
        `    <xhtml:link rel="alternate" hreflang="${lang}" href="${xmlEscape(siteUrl + url)}"/>`,
    )
    .join('\n');
}

/**
 * The home pages and every docs page that has its own file in its language,
 * with the date of the last commit to the page and its language alternates.
 * English fallbacks in the Chinese tree are noindex and not listed.
 */
function sitemap() {
  const entries: string[] = [];
  const entry = (
    url: string,
    alternates: Partial<Record<Locale, string>>,
    date: Date | undefined,
    priority: string,
  ) =>
    entries.push(
      [
        '  <url>',
        `    <loc>${xmlEscape(siteUrl + url)}</loc>`,
        ...(date ? [`    <lastmod>${isoDate(date)}</lastmod>`] : []),
        `    <priority>${priority}</priority>`,
        alternateLinks(alternates),
        '  </url>',
      ].join('\n'),
    );

  // The home pages change with the docs and the benchmark data.
  const newest = source
    .getPages()
    .filter(isOwn)
    .map(lastModifiedOf)
    .filter((date): date is Date => date !== undefined)
    .sort((a, b) => b.getTime() - a.getTime())[0];
  for (const locale of locales)
    entry(`/${locale}`, { en: '/en', zh: '/zh' }, newest, '1.0');

  for (const locale of locales) {
    for (const page of source.getPages(locale).filter(isOwn)) {
      const depth = page.slugs.length;
      entry(
        page.url,
        {
          en: getOwnPage(page.slugs, 'en')?.url,
          zh: getOwnPage(page.slugs, 'zh')?.url,
        },
        lastModifiedOf(page),
        depth === 0 ? '0.9' : depth === 1 ? '0.8' : '0.7',
      );
    }
  }

  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${entries.join('\n')}
</urlset>
`;
}

/**
 * Every crawler may read the docs, including AI crawlers and answer engines:
 * the goal is to be found and cited. They are listed by name so the policy is
 * explicit (and so a default that blocks one of them cannot apply). Note that
 * Cloudflare's "Block AI bots" setting and its managed robots.txt can
 * override this file; see README.md.
 */
const crawlers = [
  // Search engines (Baiduspider: Chinese readers find the zh pages through Baidu)
  'Googlebot',
  'Bingbot',
  'Baiduspider',
  'YandexBot',
  'DuckDuckBot',
  'Applebot',
  'Sogou web spider',
  '360Spider',
  // AI search, assistants and training
  'OAI-SearchBot',
  'ChatGPT-User',
  'GPTBot',
  'Claude-SearchBot',
  'Claude-User',
  'ClaudeBot',
  'PerplexityBot',
  'Perplexity-User',
  'Google-Extended',
  'Applebot-Extended',
  'Bytespider',
  'Amazonbot',
  'meta-externalagent',
  'DuckAssistBot',
  'MistralAI-User',
  'cohere-ai',
  'CCBot',
];

function robots() {
  return `# EasyEcs documentation: every crawler, search engines and AI crawlers alike,
# may read and cite these docs. Markdown copies of each page are listed in
# ${siteUrl}/llms.txt.

User-agent: *
${crawlers.map((name) => `User-agent: ${name}`).join('\n')}
Allow: /
Disallow: /api/

Sitemap: ${siteUrl}/sitemap.xml
`;
}

/* ------------------------------------------------------------------ */
/* llms.txt                                                            */
/* ------------------------------------------------------------------ */

const oneWorld = benchNumber('tick.oneWorld');
const oneWorldAlloc = benchNumber('tick.oneWorld.alloc');
const peerRatios = {
  friflo: benchNumber('peers.friflo.ratio'),
  arch: benchNumber('peers.arch.ratio'),
  defaultecs: benchNumber('peers.defaultecs.ratio'),
};
const hasPeers = Object.values(peerRatios).every(Boolean);
/** "Fastest" only while the data says so */
const fastestOnTick =
  hasPeers && Object.values(peerRatios).every((r) => (r?.value ?? 0) > 1);

/** The head-to-head comparisons: counts, the biggest leads, where EasyEcs is behind */
const features = featureCounts();
const featureFact = (lang: Locale) => {
  const leads = featureComparison('lead')
    .slice(0, 6)
    .map((l) => `${l.short[lang]} ${formatRatio(l.ratio)}`);
  const behind = featureComparison('behind').map((b) => b.short[lang]);
  const url = `${siteUrl}/${lang}/docs/comparison.md`;
  return lang === 'zh'
    ? `与 Friflo.Engine.ECS、Arch、DefaultEcs 的 ${features.total} 项正面对比（每个库用自己惯用的写法做同样的事，setup 中校验结果一致）：EasyEcs 在 ${features.ahead} 项更快，${features.even} 项差距在测量误差之内，${features.slower} 项更慢。领先次快的库最多的是：${leads.join('、')}。其他库领先的项目：${behind.join('、')}，原因见[横向对比](${url})。`
    : `In ${features.total} head-to-head benchmarks against Friflo.Engine.ECS, Arch and DefaultEcs (each library doing the same work its own idiomatic way, identical results checked in setup), EasyEcs is faster in ${features.ahead}, within the measurement error in ${features.even} and slower in ${features.slower}. Its biggest leads over the next-fastest library: ${leads.join(', ')}. Another library comes out ahead on ${behind.join(', ')}; why is on [Comparison](${url}).`;
};

const llmsCopy: Record<Locale, { facts: string[]; optional: string[] }> = {
  en: {
    facts: [
      ...(features.total ? [featureFact('en')] : []),
      ...(oneWorld
        ? [
            `Performance: one server tick of 9 systems over 100,000 entities takes ${oneWorld.text}${oneWorldAlloc ? ` and allocates ${oneWorldAlloc.text}` : ''} (${sourceLine(false)}) See [Performance](${siteUrl}/en/docs/performance.md).`,
          ]
        : []),
      ...(hasPeers
        ? [
            `${fastestOnTick ? 'Fastest of the C# ECS libraries measured on a full server tick' : 'Compared with the other C# ECS libraries on the same tick'}: Friflo.Engine.ECS takes ${peerRatios.friflo?.text} the time of EasyEcs, Arch ${peerRatios.arch?.text} and DefaultEcs ${peerRatios.defaultecs?.text}; Friflo is the closest, and it runs on Unity and older .NET, which EasyEcs does not. Feature matrix and every head-to-head benchmark: [Comparison](${siteUrl}/en/docs/comparison.md).`,
          ]
        : []),
      `Install: \`${installCommand}\` in a net10.0 project ([NuGet](${nugetUrl})); the package includes the source generator, the analyzer and its code fixes.`,
      'Usage: components are structs implementing `IComponent` (empty structs are tags, `ISingletonComponent` for per-world state); a system is a class deriving from `EcsSystem` with `[ForEach]` methods; `world.AddSystem<T>()`, `world.CreateEntity(...)`, `world.Update()`.',
      `Project layout: \`dotnet new install EasyEcs.Templates\` and \`dotnet new easyecs\` create a server with \`Components/\`, one system per file in \`Systems/\` and \`Program.cs\` as the host ([Structuring a real project](${siteUrl}/en/docs/guide/project-structure.md)). Debugging: \`world.DescribeTables()\`, \`entity.Describe()\`, \`system.Stats\` and \`system.Error\` ([Debugging and diagnostics](${siteUrl}/en/docs/guide/diagnostics.md)).`,
      'EasyEcs targets net10.0 only; it does not run on Unity or older .NET runtimes.',
      `These docs describe EasyEcs ${docsVersion}. Source code and issues: ${githubUrl} (MIT license).`,
      `Full text of every page: [llms-full.txt](${siteUrl}/llms-full.txt); Chinese: [llms.zh.txt](${siteUrl}/llms.zh.txt)`,
      'Each page below is also served as HTML at the same URL without `.md`.',
    ],
    optional: [
      `- [Source code on GitHub](${githubUrl}): the library, its tests, benchmarks and these docs (docs/content)`,
      `- [NuGet package](${nugetUrl}): EasyEcs, with the source generator and the analyzer`,
    ],
  },
  zh: {
    facts: [
      // URLs are Markdown links: bare URLs followed by full-width punctuation
      // (。）：) are misread by link extractors.
      ...(features.total ? [featureFact('zh')] : []),
      ...(oneWorld
        ? [
            `性能：10 万实体、9 个系统的一个服务器 tick 耗时 ${oneWorld.text}${oneWorldAlloc ? `，分配 ${oneWorldAlloc.text}` : ''}（${sourceLine(true)}）参见[性能](${siteUrl}/zh/docs/performance.md)。`,
          ]
        : []),
      ...(hasPeers
        ? [
            `${fastestOnTick ? '在完整服务器 tick 上是实测最快的 C# ECS' : '与其他 C# ECS 框架在同一个 tick 上对比'}：Friflo.Engine.ECS 的耗时是 EasyEcs 的 ${peerRatios.friflo?.text}，Arch 是 ${peerRatios.arch?.text}，DefaultEcs 是 ${peerRatios.defaultecs?.text}；Friflo 最接近，而且支持 Unity 和更老的 .NET，EasyEcs 不支持。功能矩阵与全部正面对比：[横向对比](${siteUrl}/zh/docs/comparison.md)。`,
          ]
        : []),
      `安装：在 net10.0 项目中运行 \`${installCommand}\`（[NuGet](${nugetUrl})），包内已包含源生成器、分析器和代码修复。`,
      '用法：组件是实现 `IComponent` 的结构体（空结构体即标签，World 级状态用 `ISingletonComponent`）；系统是继承 `EcsSystem` 的类，逻辑写在 `[ForEach]` 方法里；然后调用 `world.AddSystem<T>()`、`world.CreateEntity(...)`、`world.Update()`。',
      `项目结构：\`dotnet new install EasyEcs.Templates\` 后运行 \`dotnet new easyecs\`，生成带 \`Components/\`、\`Systems/\`（一个文件一个系统）和宿主 \`Program.cs\` 的服务器（[项目结构](${siteUrl}/zh/docs/guide/project-structure.md)）。调试：\`world.DescribeTables()\`、\`entity.Describe()\`、\`system.Stats\` 与 \`system.Error\`（[调试与诊断](${siteUrl}/zh/docs/guide/diagnostics.md)）。`,
      'EasyEcs 只面向 net10.0，不支持 Unity 和更老的 .NET 运行时。',
      `本文档对应 EasyEcs ${docsVersion}。源码与问题反馈：[GitHub](${githubUrl})（MIT 协议）。`,
      `全部页面全文：[llms-full.zh.txt](${siteUrl}/llms-full.zh.txt)。英文版：[llms.txt](${siteUrl}/llms.txt)`,
      '下列每个页面去掉 `.md` 即为对应的 HTML 页面；尚无中文版的页面链接到英文原文。',
    ],
    optional: [
      `- [GitHub 源码](${githubUrl})：库、测试、基准测试与本文档（docs/content）`,
      `- [NuGet 包](${nugetUrl})：EasyEcs，含源生成器与分析器`,
    ],
  },
};

interface Section {
  name: string;
  pages: Page[];
}

/**
 * Sidebar names of folder index pages ("Advanced Guides" rather than the
 * page's own title, "Overview"), so llms.txt links read well out of context.
 */
const folderLabels = new Map<string, string>();

/** The English page shown at a fallback URL; the page itself otherwise */
function ownOrEnglish(page: Page) {
  return isOwn(page) ? page : (getOwnPage(page.slugs, 'en') ?? page);
}

/** The pages of a language, grouped and ordered like the sidebar. */
function sidebarSections(locale: Locale): Section[] {
  const byUrl = new Map(source.getPages(locale).map((p) => [p.url, p]));
  const sections: Section[] = [];
  const seen = new Set<string>();
  let current: Section | undefined;

  function add(url: string | undefined) {
    const page = url ? byUrl.get(url) : undefined;
    if (!page || seen.has(page.url)) return;
    seen.add(page.url);
    if (!current) {
      current = { name: locale === 'zh' ? '文档' : 'Docs', pages: [] };
      sections.push(current);
    }
    current.pages.push(page);
  }

  function walk(nodes: PageTree.Node[]) {
    for (const node of nodes) {
      if (node.type === 'separator') {
        current = { name: String(node.name ?? ''), pages: [] };
        sections.push(current);
      } else if (node.type === 'page') {
        add(node.url);
      } else if (node.type === 'folder') {
        if (node.index && typeof node.name === 'string')
          folderLabels.set(node.index.url, node.name);
        add(node.index?.url);
        walk(node.children);
      }
    }
  }
  add(`/${locale}/docs`);
  walk(source.getPageTree(locale).children);
  // Pages missing from the sidebar still belong in the index.
  const rest = source.getPages(locale).filter((page) => !seen.has(page.url));
  if (rest.length > 0)
    sections.push({ name: locale === 'zh' ? '更多' : 'More', pages: rest });
  return sections.filter((section) => section.pages.length > 0);
}

/** llms.txt (https://llmstxt.org): summary, key facts and the Markdown pages */
function llmsList(locale: Locale) {
  const t = llmsCopy[locale];
  const lines = [
    '# EasyEcs',
    '',
    `> ${productSummary[locale]}`,
    '',
    ...t.facts.map((fact) => `- ${fact}`),
    '',
  ];
  for (const section of sidebarSections(locale)) {
    lines.push(`## ${section.name}`, '');
    for (const page of section.pages) {
      // A page with no Chinese file yet links to the English Markdown.
      const shown = ownOrEnglish(page);
      const description = shown.data.description
        ? `: ${shown.data.description}`
        : '';
      const note = shown === page ? '' : locale === 'zh' ? '（英文）' : '';
      lines.push(
        `- [${folderLabels.get(page.url) ?? shown.data.title}${note}](${siteUrl}${markdownPath(shown.url)})${description}`,
      );
    }
    lines.push('');
  }
  lines.push('## Optional', '', ...t.optional, '');
  return `${lines.join('\n')}\n`;
}

/** Every page, in sidebar order, as one document (English where no Chinese page exists yet) */
async function llmsFull(locale: Locale) {
  const pages = sidebarSections(locale).flatMap((section) =>
    section.pages.map(ownOrEnglish),
  );
  const header = [
    `# EasyEcs ${docsVersion} ${locale === 'zh' ? '文档' : 'documentation'}`,
    '',
    `> ${productSummary[locale]}`,
    '',
  ].join('\n');
  const bodies = await Promise.all(pages.map(markdownOf));
  return `${header}\n${bodies.join('\n\n---\n\n')}`;
}

/* ------------------------------------------------------------------ */
/* Social cards                                                        */
/* ------------------------------------------------------------------ */

function cards(): Card[] {
  const list: Card[] = homeCards();
  // Fallback pages use the English page's card (src/lib/seo.ts).
  for (const page of source.getPages().filter(isOwn)) {
    const lang = localeOf(page);
    list.push({
      url: page.url,
      lang,
      eyebrow: lang === 'zh' ? 'EasyEcs 文档' : 'EasyEcs Docs',
      title: page.data.title,
      description: docDescription({
        lang,
        title: page.data.title,
        description: page.data.description,
      }),
    });
  }
  return list;
}

/* ------------------------------------------------------------------ */

async function main() {
  // Cloudflare serves /404.html (status 404) for unknown paths. It is shown at
  // any URL, so it must not hydrate: the router would try to load that URL.
  const notFoundDir = path.join(outDir, '404');
  const notFound = await fs.readFile(
    path.join(notFoundDir, 'index.html'),
    'utf8',
  );
  await write(
    '404.html',
    notFound
      .replace(/<link rel="modulepreload"[^>]*>/g, '')
      .replace(/<script type="module"[\s\S]*?<\/script>/g, '')
      .replace(/<script>[^<]*__reactRouter[\s\S]*?<\/script>/g, ''),
  );
  await fs.rm(notFoundDir, { recursive: true, force: true });

  const moved = await flattenPages(outDir);

  // The prerender also writes a .data file (the same JSON, for client-side
  // navigation) next to each search index: nothing requests it.
  for (const locale of locales)
    await fs.rm(path.join(outDir, `api/search/${locale}.json.data`), {
      force: true,
    });

  const tasks: Promise<void>[] = [
    write('sitemap.xml', sitemap()),
    write('robots.txt', robots()),
  ];
  for (const locale of locales) {
    const list = llmsList(locale);
    const full = await llmsFull(locale);
    const suffix = locale === 'en' ? '' : `.${locale}`;
    tasks.push(
      write(`llms${suffix}.txt`, list),
      write(`llms-full${suffix}.txt`, full),
      // Also under the language prefix: /zh/llms.txt
      write(`${locale}/llms.txt`, list),
      write(`${locale}/llms-full.txt`, full),
    );
  }

  // Markdown next to each page (/en/docs/guide/queries.md), for LLMs and the
  // "Copy Markdown" page action; .mdx is served too. Fallback pages have none:
  // their page actions and alternate links point at the English copy.
  for (const page of source.getPages().filter(isOwn)) {
    const markdown = await markdownOf(page);
    tasks.push(
      write(`${markdownPath(page.url).slice(1)}`, markdown),
      write(`${page.url.slice(1)}.mdx`, markdown),
    );
  }

  await Promise.all(tasks);
  await writeCards(cards(), outDir, path.resolve('public/og.png'));

  console.log(
    `Wrote static files to ${path.relative('.', outDir)} (${moved} pages moved to .html)`,
  );
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
