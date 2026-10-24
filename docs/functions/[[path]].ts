interface Env {
  ASSETS: Fetcher;
}

const localizedPathPattern = /^\/(en|zh)(?:\/|$)/;
const docsMarkdownPattern = /^\/(en|zh)\/docs(\/.+)?$/;
const homePattern = /^\/(en|zh)\/?$/;
const markdownMime = 'text/markdown; charset=utf-8';
const markdownFilePattern = /^\/(en|zh)\/docs(\/.+)?\.mdx?$/;
const siteUrl = 'https://ecs.xgamedev.net';
const localeCookieName = 'FD_LOCALE';
const zhCountries = new Set(['CN', 'TW', 'HK', 'MO', 'SG']);
const bypassExtensions = new Set([
  '.css',
  '.js',
  '.mjs',
  '.map',
  '.png',
  '.jpg',
  '.jpeg',
  '.gif',
  '.svg',
  '.ico',
  '.webp',
  '.avif',
  '.woff',
  '.woff2',
  '.ttf',
  '.otf',
  '.json',
  '.txt',
  '.xml',
  '.webmanifest',
]);

export const onRequest: PagesFunction<Env> = async (context) => {
  const { request } = context;

  if (request.method !== 'GET' && request.method !== 'HEAD') {
    return context.next();
  }

  const url = new URL(request.url);

  if (shouldBypass(url.pathname)) {
    return context.next();
  }

  // Markdown copies of the docs pages (/en/docs/guide/queries.md, also .mdx)
  if (markdownFilePattern.test(url.pathname)) {
    return withMarkdownHeaders(await context.next(), url.pathname);
  }

  // React Router data files: never a search result
  if (url.pathname.endsWith('.data')) {
    return withHeaders(await context.next(), {
      'X-Robots-Tag': 'noindex',
    });
  }

  if (prefersMarkdown(request) && docsMarkdownPattern.test(url.pathname)) {
    return serveMarkdown(context, url);
  }

  // The home pages in Markdown are their language's llms.txt: what EasyEcs
  // is, the key facts and every page.
  const home = homePattern.exec(url.pathname);
  if (home && prefersMarkdown(request)) {
    const response = await context.env.ASSETS.fetch(
      new URL(`/${home[1]}/llms.txt`, url),
    );
    if (response.ok)
      return withHeaders(response, {
        'Content-Type': markdownMime,
        Link: `<${siteUrl}/${home[1]}>; rel="canonical"`,
        Vary: 'Accept',
      });
  }

  if (!localizedPathPattern.test(url.pathname)) {
    const locale = getLocale(request);
    const target = new URL(request.url);
    target.pathname = localizePath(url.pathname, locale);

    return new Response(null, {
      status: 302,
      headers: {
        Location: `${target.pathname}${target.search}`,
        'Set-Cookie': serializeLocaleCookie(locale),
      },
    });
  }

  // Docs and home pages answer with HTML or Markdown depending on Accept (above)
  if (
    docsMarkdownPattern.test(url.pathname) ||
    homePattern.test(url.pathname)
  ) {
    return withHeaders(await context.next(), { Vary: 'Accept' });
  }

  return context.next();
};

function shouldBypass(pathname: string) {
  if (
    pathname.startsWith('/assets/') ||
    pathname.startsWith('/images/') ||
    // the demo's .NET runtime (public/demo): served as is, never localized
    pathname.startsWith('/demo/') ||
    pathname === '/robots.txt' ||
    pathname === '/sitemap.xml' ||
    pathname === '/static.json' ||
    pathname === '/manifest.webmanifest'
  ) {
    return true;
  }

  const extension = pathname.match(/\.[a-z0-9]+$/i)?.[0].toLowerCase();
  return extension ? bypassExtensions.has(extension) : false;
}

function prefersMarkdown(request: Request) {
  const accept = request.headers.get('accept');
  if (!accept) return false;

  const parts = accept.split(',').map((part) => part.trim().toLowerCase());
  const markdownIndex = parts.findIndex((part) =>
    part.startsWith('text/markdown'),
  );
  if (markdownIndex === -1) return false;

  const htmlIndex = parts.findIndex((part) => part.startsWith('text/html'));
  return htmlIndex === -1 || markdownIndex < htmlIndex;
}

async function serveMarkdown(
  context: EventContext<Env, string, unknown>,
  url: URL,
) {
  const assetUrl = new URL(url);
  // The Markdown copy of /en/docs/guide/queries (or .../queries/) is
  // /en/docs/guide/queries.md; /en/docs is /en/docs.md.
  const pathname = url.pathname.replace(/\/+$/, '');
  assetUrl.pathname = /\.mdx?$/.test(pathname) ? pathname : `${pathname}.md`;

  const response = await context.env.ASSETS.fetch(assetUrl);
  if (response.status === 404) return context.next();

  const markdown = withMarkdownHeaders(response, assetUrl.pathname);
  markdown.headers.set('Vary', 'Accept');
  return markdown;
}

function withHeaders(response: Response, headers: Record<string, string>) {
  const result = new Response(response.body, response);
  for (const [name, value] of Object.entries(headers))
    result.headers.set(name, value);
  return result;
}

/**
 * Markdown copies are served as text/markdown and point search engines at the
 * HTML page (rel="canonical"), so they never compete with it in results.
 */
function withMarkdownHeaders(response: Response, pathname: string) {
  if (!response.ok) return response;
  const page = pathname.replace(/\.mdx?$/, '');
  return withHeaders(response, {
    'Content-Type': markdownMime,
    'Cache-Control':
      response.headers.get('cache-control') ?? 'public, max-age=3600',
    Link: `<${siteUrl}${page}>; rel="canonical"`,
  });
}

function getLocale(request: Request) {
  const cookieLocale = request.headers
    .get('cookie')
    ?.split(';')
    .map((cookie) => cookie.trim())
    .find((cookie) => cookie.startsWith(`${localeCookieName}=`))
    ?.split('=')[1];

  if (cookieLocale === 'en' || cookieLocale === 'zh') return cookieLocale;

  const cf = request.cf as IncomingRequestCfProperties | undefined;
  const country =
    typeof cf?.country === 'string'
      ? cf.country
      : request.headers.get('cf-ipcountry');

  return country && zhCountries.has(country.toUpperCase()) ? 'zh' : 'en';
}

/**
 * `/` -> `/en`, `/docs/guide/threading` -> `/en/docs/guide/threading`: the
 * library's comments and README link to locale-less `/docs/...` URLs.
 */
function localizePath(pathname: string, locale: 'en' | 'zh') {
  // `/index.html` is the root page, not a page under /en (which would 404)
  if (pathname === '/' || pathname === '/index.html') return `/${locale}`;
  return `/${locale}${pathname.replace(/\/+$/, '')}`;
}

function serializeLocaleCookie(locale: 'en' | 'zh') {
  return `${localeCookieName}=${locale}; Path=/; Max-Age=31536000; SameSite=Lax; Secure`;
}
