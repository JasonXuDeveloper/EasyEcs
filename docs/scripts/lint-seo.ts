/**
 * Checks the built site (build/client) for what search engines and answer
 * engines read, so a regression fails the build:
 *
 * - every page: one <title> (unique, at most 60 characters; a Chinese
 *   character counts as two, the width search results cut at), a meta
 *   description (English 70-160 characters, Chinese 40-90), robots, a
 *   canonical link, hreflang en / zh / zh-CN / x-default on pages with both
 *   languages, Open Graph and Twitter tags whose image exists, and JSON-LD
 *   that parses: TechArticle and BreadcrumbList on docs pages, FAQPage on
 *   `faq: true` pages, WebSite, SoftwareSourceCode and SoftwareApplication on
 *   the home pages;
 * - sitemap.xml lists every indexable page (with lastmod and alternates) and
 *   nothing else; robots.txt allows the AI crawlers and names the sitemap;
 * - llms.txt / llms-full.txt and their Chinese versions link or contain every
 *   page, and every indexable docs page has its Markdown copy.
 *
 * Usage: `bun run lint:seo` after `react-router build` and generate:static
 * (`bun run build` runs it last).
 */
import { existsSync, readFileSync } from 'node:fs';
import * as path from 'node:path';
import { docExtras, isFallback, source } from '../src/lib/source';

const outDir = path.resolve('build/client');
const siteUrl = 'https://ecs.xgamedev.net';

type Page = ReturnType<typeof source.getPages>[number];

/**
 * Pages whose copy is being rewritten elsewhere: their problems are printed
 * as warnings until the rewrite lands. A listed page that passes is an error,
 * so the list only shrinks.
 */
const pending = new Set<string>();

const errors: string[] = [];
const warnings: string[] = [];
const fail = (url: string, message: string) =>
  (pending.has(url) ? warnings : errors).push(`${url}: ${message}`);
/** Dates come from git: a page not committed yet has none. */
const warn = (url: string, message: string) =>
  warnings.push(`${url}: ${message}`);

/** Search results cut titles by width: a CJK character is about two Latin ones. */
function width(text: string) {
  let w = 0;
  for (const ch of text) w += /[⺀-鿿＀-￯]/u.test(ch) ? 2 : 1;
  return w;
}

function decode(value: string) {
  return value
    .replace(/&quot;/g, '"')
    .replace(/&#x27;|&#39;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');
}

interface Head {
  titles: string[];
  meta: Map<string, string[]>;
  links: { rel: string; href: string; hreflang?: string; type?: string }[];
  jsonLd: unknown[];
  jsonLdErrors: string[];
}

function attrs(tag: string) {
  const result: Record<string, string> = {};
  for (const m of tag.matchAll(/([a-zA-Z:-]+)="([^"]*)"/g))
    result[m[1].toLowerCase()] = decode(m[2]);
  return result;
}

function parseHead(html: string): Head {
  const head = html.slice(0, html.indexOf('</head>'));
  const titles = [...head.matchAll(/<title>([\s\S]*?)<\/title>/g)].map((m) =>
    decode(m[1]),
  );
  const meta = new Map<string, string[]>();
  for (const m of head.matchAll(/<meta\s[^>]*>/g)) {
    const a = attrs(m[0]);
    const key = a.property ?? a.name;
    if (key && a.content !== undefined)
      meta.set(key, [...(meta.get(key) ?? []), a.content]);
  }
  const links = [...head.matchAll(/<link\s[^>]*>/g)].map((m) => {
    const a = attrs(m[0]);
    return { rel: a.rel, href: a.href, hreflang: a.hreflang, type: a.type };
  });
  const jsonLd: unknown[] = [];
  const jsonLdErrors: string[] = [];
  for (const m of head.matchAll(
    /<script type="application\/ld\+json">([\s\S]*?)<\/script>/g,
  )) {
    try {
      jsonLd.push(JSON.parse(m[1]));
    } catch (error) {
      jsonLdErrors.push((error as Error).message);
    }
  }
  return { titles, meta, links, jsonLd, jsonLdErrors };
}

/** Every node of the JSON-LD graphs, nested ones included */
function nodes(value: unknown, into: Record<string, unknown>[] = []) {
  if (Array.isArray(value)) for (const v of value) nodes(v, into);
  else if (value && typeof value === 'object') {
    const node = value as Record<string, unknown>;
    if (typeof node['@type'] === 'string') into.push(node);
    for (const v of Object.values(node)) nodes(v, into);
  }
  return into;
}

const localeOf = (page: Page) => (page.locale === 'zh' ? 'zh' : 'en');
const ownPages = source.getPages().filter((p) => !isFallback(p, localeOf(p)));
const ownUrls = new Set(ownPages.map((p) => p.url));
const hasBoth = (slugs: string[]) =>
  ['en', 'zh'].every((lang) =>
    ownUrls.has(source.getPage(slugs, lang)?.url ?? ''),
  );

interface Target {
  url: string;
  lang: 'en' | 'zh';
  indexable: boolean;
  canonical: string;
  both: boolean;
  kind: 'home' | 'doc';
  faq: boolean;
}

const targets: Target[] = [
  ...(['en', 'zh'] as const).map(
    (lang): Target => ({
      url: `/${lang}`,
      lang,
      indexable: true,
      canonical: `/${lang}`,
      both: true,
      kind: 'home',
      faq: true,
    }),
  ),
  ...source.getPages().map((page): Target => {
    const lang = localeOf(page);
    const own = !isFallback(page, lang);
    const english = source.getPage(page.slugs, 'en');
    return {
      url: page.url,
      lang,
      indexable: own,
      canonical: own ? page.url : (english?.url ?? page.url),
      both: hasBoth(page.slugs),
      kind: 'doc',
      faq: own && Boolean(docExtras(page).faq),
    };
  }),
];

const titleOwners = new Map<string, string>();
const descriptionOwners = new Map<string, string>();

for (const t of targets) {
  const file = path.join(outDir, `${t.url.slice(1)}.html`);
  if (!existsSync(file)) {
    fail(t.url, `no ${path.relative(outDir, file)}`);
    continue;
  }
  const head = parseHead(readFileSync(file, 'utf8'));
  const one = (key: string) => {
    const values = head.meta.get(key) ?? [];
    if (values.length !== 1)
      fail(t.url, `${values.length} <meta ${key}> (expected 1)`);
    return values[0] ?? '';
  };

  // Title
  if (head.titles.length !== 1)
    fail(t.url, `${head.titles.length} <title> elements`);
  const title = head.titles[0] ?? '';
  const description = one('description');
  // A noindex page (an English page under /zh/) is judged at its canonical URL.
  if (t.indexable) {
    if (width(title) > 60)
      fail(t.url, `title is ${width(title)} wide (max 60): "${title}"`);
    const length = [...description].length;
    const [min, max] = t.lang === 'zh' ? [40, 90] : [70, 160];
    if (length < min || length > max)
      fail(
        t.url,
        `description has ${length} characters (${min}-${max}): "${description}"`,
      );
  }

  if (t.indexable) {
    const previous = titleOwners.get(title);
    if (previous) fail(t.url, `same title as ${previous}: "${title}"`);
    titleOwners.set(title, t.url);
    const prevDesc = descriptionOwners.get(description);
    if (prevDesc) fail(t.url, `same description as ${prevDesc}`);
    descriptionOwners.set(description, t.url);
  }

  // Robots and canonical
  const robots = one('robots');
  if (t.indexable !== robots.startsWith('index'))
    fail(
      t.url,
      `robots "${robots}" on a page that is ${t.indexable ? '' : 'not '}indexable`,
    );
  const canonicals = head.links.filter((l) => l.rel === 'canonical');
  if (canonicals.length !== 1 || canonicals[0].href !== siteUrl + t.canonical)
    fail(
      t.url,
      `canonical ${canonicals.map((l) => l.href).join(', ') || 'missing'} (expected ${siteUrl}${t.canonical})`,
    );

  // hreflang
  const alternates = new Map(
    head.links
      .filter((l) => l.rel === 'alternate' && l.hreflang)
      .map((l) => [l.hreflang as string, l.href]),
  );
  if (t.indexable && t.both) {
    for (const lang of ['en', 'zh', 'zh-CN', 'x-default'])
      if (!alternates.get(lang)?.startsWith(siteUrl))
        fail(t.url, `no hreflang ${lang}`);
    if (alternates.get(t.lang === 'zh' ? 'zh' : 'en') !== siteUrl + t.url)
      fail(t.url, 'hreflang of its own language is not the page');
  }
  if (!t.indexable && alternates.size > 0)
    fail(t.url, 'hreflang on a noindex page');

  // Open Graph and Twitter
  for (const key of [
    'og:title',
    'og:description',
    'og:url',
    'og:type',
    'og:locale',
    'og:image',
    'og:image:alt',
    'twitter:card',
    'twitter:title',
    'twitter:description',
    'twitter:image',
  ])
    one(key);
  if ((head.meta.get('og:url') ?? [])[0] !== siteUrl + t.url)
    fail(t.url, 'og:url is not the page');
  const image = (head.meta.get('og:image') ?? [])[0] ?? '';
  if (image !== `${siteUrl}/og${t.canonical}.png`)
    fail(t.url, `og:image ${image} is not the page's card`);
  else if (!existsSync(path.join(outDir, image.slice(siteUrl.length))))
    fail(t.url, `og:image ${image} was not written`);

  // JSON-LD
  for (const message of head.jsonLdErrors)
    fail(t.url, `JSON-LD does not parse: ${message}`);
  if (head.jsonLd.length === 0) fail(t.url, 'no JSON-LD');
  const types = new Set(nodes(head.jsonLd).map((n) => n['@type'] as string));
  const expected =
    t.kind === 'home'
      ? ['WebSite', 'WebPage', 'SoftwareSourceCode', 'SoftwareApplication']
      : ['WebSite', 'TechArticle', 'BreadcrumbList'];
  if (t.faq) expected.push('FAQPage');
  for (const type of expected)
    if (!types.has(type)) fail(t.url, `JSON-LD has no ${type}`);
  for (const node of nodes(head.jsonLd)) {
    if (node['@type'] === 'FAQPage') {
      const questions = node.mainEntity as unknown[];
      if (!Array.isArray(questions) || questions.length === 0)
        fail(t.url, 'FAQPage without questions');
    }
    if (node['@type'] === 'TechArticle') {
      for (const key of ['headline', 'description', 'image'])
        if (!node[key]) fail(t.url, `TechArticle has no ${key}`);
      if (!node.dateModified)
        warn(t.url, 'TechArticle has no dateModified (not committed?)');
    }
  }
}

/* sitemap.xml ------------------------------------------------------- */

const sitemap = readFileSync(path.join(outDir, 'sitemap.xml'), 'utf8');
const entries = new Map<string, string>();
for (const m of sitemap.matchAll(/<url>([\s\S]*?)<\/url>/g)) {
  const loc = /<loc>([^<]+)<\/loc>/.exec(m[1])?.[1] ?? '';
  entries.set(loc.slice(siteUrl.length), m[1]);
}
for (const t of targets) {
  const entry = entries.get(t.url);
  if (!t.indexable) {
    if (entry) fail('sitemap.xml', `lists the noindex page ${t.url}`);
    continue;
  }
  if (!entry) {
    fail('sitemap.xml', `does not list ${t.url}`);
    continue;
  }
  if (!/<lastmod>\d{4}-\d{2}-\d{2}<\/lastmod>/.test(entry))
    warn('sitemap.xml', `${t.url} has no lastmod (not committed?)`);
  if (t.both)
    for (const lang of ['en', 'zh', 'zh-CN', 'x-default'])
      if (!entry.includes(`hreflang="${lang}"`))
        fail('sitemap.xml', `${t.url} has no ${lang} alternate`);
}
for (const url of entries.keys())
  if (!targets.some((t) => t.url === url))
    fail('sitemap.xml', `lists ${url}, which is not a page`);

/* robots.txt -------------------------------------------------------- */

const robots = readFileSync(path.join(outDir, 'robots.txt'), 'utf8');
for (const bot of ['GPTBot', 'ClaudeBot', 'PerplexityBot', 'Google-Extended'])
  if (!robots.includes(`User-agent: ${bot}`))
    fail('robots.txt', `does not name ${bot}`);
if (!/^Allow: \/$/m.test(robots)) fail('robots.txt', 'no "Allow: /"');
if (!robots.includes(`Sitemap: ${siteUrl}/sitemap.xml`))
  fail('robots.txt', 'no Sitemap line');

/* llms.txt and the Markdown copies ---------------------------------- */

const llms = {
  en: readFileSync(path.join(outDir, 'llms.txt'), 'utf8'),
  zh: readFileSync(path.join(outDir, 'llms.zh.txt'), 'utf8'),
};
const llmsFull = {
  en: readFileSync(path.join(outDir, 'llms-full.txt'), 'utf8'),
  zh: readFileSync(path.join(outDir, 'llms-full.zh.txt'), 'utf8'),
};
for (const lang of ['en', 'zh'] as const) {
  const name = lang === 'en' ? 'llms.txt' : 'llms.zh.txt';
  const full = lang === 'en' ? 'llms-full.txt' : 'llms-full.zh.txt';
  if (!llms[lang].includes('dotnet add package EasyEcs'))
    fail(name, 'no install line');
  // The list stays a list: it is read whole by agents with small budgets.
  if (llms[lang].length > 60_000)
    fail(name, `${llms[lang].length} characters (max 60,000)`);
  if (llmsFull[lang].length > 3_000_000)
    fail(full, `${llmsFull[lang].length} characters (max 3,000,000)`);
}
for (const page of source.getPages()) {
  const lang = localeOf(page);
  const own = !isFallback(page, lang);
  const shown = own ? page : source.getPage(page.slugs, 'en');
  if (!shown) continue;
  const md = `${siteUrl}${shown.url}.md`;
  const name = lang === 'en' ? 'llms.txt' : 'llms.zh.txt';
  if (!llms[lang].includes(`](${md})`)) fail(name, `does not link ${md}`);
  const full = lang === 'en' ? 'llms-full.txt' : 'llms-full.zh.txt';
  if (!llmsFull[lang].includes(`URL: ${siteUrl}${shown.url}\n`))
    fail(full, `does not contain ${shown.url}`);
  if (own && !existsSync(path.join(outDir, `${page.url.slice(1)}.md`)))
    fail(page.url, 'no Markdown copy');
}

for (const url of pending)
  if (!warnings.some((w) => w.startsWith(`${url}: `)))
    errors.push(
      `${url}: passes now, remove it from \`pending\` in scripts/lint-seo.ts`,
    );
if (warnings.length > 0)
  console.warn(
    `${warnings.join('\n')}\n(${warnings.length} warning(s): pending pages, uncommitted dates)\n`,
  );
if (errors.length > 0) {
  console.error(errors.join('\n'));
  console.error(`\n${errors.length} SEO problem(s) in ${outDir}`);
  process.exit(1);
}
console.log(
  `SEO: ${targets.length} pages, sitemap (${entries.size} URLs), robots.txt, llms files and Markdown copies OK`,
);
