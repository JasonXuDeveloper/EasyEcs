/**
 * Mermaid diagrams are drawn at build time (Mermaid in a headless Chromium
 * through Playwright, on mermaid-isomorphic's page): the prerendered page holds
 * the SVG and the browser loads no Mermaid at all. Build time only (imported by
 * source.config.ts).
 */
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import { availableParallelism } from 'node:os';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import type { Element, ElementContent, Root as HastRoot } from 'hast';
import { fromHtmlIsomorphic } from 'hast-util-from-html-isomorphic';
import type { Code, Root } from 'mdast';
import type { MdxJsxFlowElement } from 'mdast-util-mdx-jsx';
import type { Mermaid, MermaidConfig } from 'mermaid';
import { type BrowserContext, chromium } from 'playwright';
import { visit } from 'unist-util-visit';
import type { VFile } from 'vfile';

const renderOptions = {
  // One drawing for both themes: `neutral` is grayscale, and the dark site
  // shows it inverted (src/components/mermaid.tsx).
  mermaidConfig: {
    theme: 'neutral',
    securityLevel: 'strict',
    // The site's text font (Tailwind's --font-sans): labels are measured
    // with the build's fonts and shown with the reader's.
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', 'Noto Sans', Arial, sans-serif",
  } satisfies MermaidConfig,
};

/**
 * Fumadocs' remarkMdxMermaid turns a fence into `<Mermaid chart="..." />`;
 * this puts the fence back inside it, for rehypeMermaid to replace with the
 * SVG. The `chart` attribute stays for the Markdown copies
 * (src/lib/markdown.tsx).
 */
export function remarkMermaidSource() {
  return (tree: Root) => {
    visit(tree, 'mdxJsxFlowElement', (node: MdxJsxFlowElement) => {
      if (node.name !== 'Mermaid') return;
      const chart = node.attributes.find(
        (a) => a.type === 'mdxJsxAttribute' && a.name === 'chart',
      )?.value;
      if (typeof chart !== 'string') return;
      const fence: Code = { type: 'code', lang: 'mermaid', value: chart };
      node.children = [fence];
    });
  };
}

/*
 * Drawing is the slow part of the build. The SVGs of a page are cached by the
 * page's diagrams and the Mermaid version, in memory (the build's second
 * environment) and on disk (node_modules/.cache/mermaid, the next local build;
 * Cloudflare keeps no node_modules between builds, so it draws every diagram
 * each time). A page is the unit because Mermaid numbers the SVG ids of one
 * render (mermaid-0, mermaid-1, ...).
 */
const mermaidVersion: string = JSON.parse(
  fs.readFileSync(path.resolve('node_modules/mermaid/package.json'), 'utf8'),
).version;
const cacheDir = path.resolve('node_modules/.cache/mermaid');
const memory = new Map<string, Promise<string[]>>();

function cacheKey(diagrams: string[]) {
  return createHash('sha256')
    .update(JSON.stringify([mermaidVersion, renderOptions, diagrams]))
    .digest('hex');
}

function drawn(diagrams: string[], file: VFile): Promise<string[]> {
  const key = cacheKey(diagrams);
  let svgs = memory.get(key);
  if (!svgs) {
    svgs = fromDisk(key) ?? draw(diagrams, key, file);
    memory.set(key, svgs);
  }
  return svgs;
}

function fromDisk(key: string): Promise<string[]> | undefined {
  try {
    const svgs = JSON.parse(
      fs.readFileSync(path.join(cacheDir, `${key}.json`), 'utf8'),
    );
    return Array.isArray(svgs) ? Promise.resolve(svgs) : undefined;
  } catch {
    return undefined;
  }
}

/*
 * The pages with diagrams compile at about the same time. mermaid-isomorphic's
 * renderer opens a Chromium tab per call, and each tab loads Mermaid (13 MB of
 * script) again: two dozen tabs at once, competing with the build for the CPU
 * (26 pages: 37 s within the build, 2.7 s with this pool). Here a few tabs, one
 * per core, load Mermaid once and draw page after page, on mermaid-isomorphic's
 * page with its styles. A render initializes Mermaid again and numbers its ids
 * from 0, so a tab's tenth page gets the SVGs a fresh tab would draw. The
 * browser closes when the queue runs dry, as mermaid-isomorphic's does.
 */
const tabs = Math.min(4, availableParallelism());
const isomorphic = createRequire(
  path.resolve('node_modules/mermaid-isomorphic/package.json'),
);
const fileUrl = (specifier: string) =>
  pathToFileURL(isomorphic.resolve(specifier)).href;

interface Job {
  diagrams: string[];
  resolve: (svgs: (string | { error: string })[]) => void;
  reject: (error: unknown) => void;
}
const queue: Job[] = [];
let browser: Promise<BrowserContext> | undefined;
let open = 0;

async function launch() {
  return (await chromium.launch()).newContext({ bypassCSP: true });
}

/** Runs in the tab: mermaid-isomorphic's render, the SVG markup only */
async function renderInTab({
  diagrams,
  config,
}: {
  diagrams: string[];
  config: MermaidConfig;
}) {
  const { mermaid } = globalThis as unknown as { mermaid: Mermaid };
  await Promise.all(Array.from(document.fonts, (font) => font.load()));
  const container = document.createElement('div');
  container.ariaHidden = 'true';
  container.style.maxHeight = '0';
  container.style.opacity = '0';
  container.style.overflow = 'hidden';
  document.body.appendChild(container);
  mermaid.registerIconPacks([]);
  mermaid.initialize(config);
  return Promise.all(
    diagrams.map(async (diagram, index) => {
      try {
        const { svg } = await mermaid.render(
          `mermaid-${index}`,
          diagram,
          container,
        );
        const root = new DOMParser().parseFromString(svg, 'text/html');
        const [element] = root.getElementsByTagName('svg');
        return new XMLSerializer().serializeToString(element);
      } catch (error) {
        return { error: String(error) };
      }
    }),
  );
}

/** One tab: takes pages off the queue until it is empty. */
async function tab() {
  open++;
  browser ??= launch();
  const context = browser;
  let job = queue.shift();
  try {
    const page = await (await context).newPage();
    await page.goto(
      pathToFileURL(path.resolve('node_modules/mermaid-isomorphic/index.html'))
        .href,
    );
    await Promise.all([
      page.addStyleTag({
        url: fileUrl('@fortawesome/fontawesome-free/css/all.css'),
      }),
      page.addStyleTag({ url: fileUrl('katex/dist/katex.css') }),
      page.addScriptTag({ url: fileUrl('mermaid/dist/mermaid.js') }),
    ]);
    const config = renderOptions.mermaidConfig;
    for (; job; job = queue.shift())
      job.resolve(
        await page.evaluate(renderInTab, { diagrams: job.diagrams, config }),
      );
  } catch (error) {
    for (; job; job = queue.shift()) job.reject(error);
  }
  // The queue is empty (no await since the last shift): the last tab closes
  // the browser, and a later page launches a new one.
  if (--open === 0 && browser === context) {
    browser = undefined;
    const ctx = await context.catch(() => undefined);
    await ctx?.browser()?.close();
  }
}

function render(diagrams: string[]) {
  return new Promise<(string | { error: string })[]>((resolve, reject) => {
    queue.push({ diagrams, resolve, reject });
    if (open < tabs) void tab();
  });
}

async function draw(diagrams: string[], key: string, file: VFile) {
  const svgs = (await render(diagrams)).map((svg) => {
    if (typeof svg === 'string') return svg;
    const message = file.message(svg.error, { source: 'mermaid' });
    message.fatal = true;
    throw message;
  });
  fs.mkdirSync(cacheDir, { recursive: true });
  fs.writeFileSync(path.join(cacheDir, `${key}.json`), JSON.stringify(svgs));
  return svgs;
}

/** The mermaid source of `<pre><code class="language-mermaid">`, else undefined */
function mermaidSource(pre: Element) {
  const [code, ...rest] = pre.children.filter(
    (child) => !(child.type === 'text' && !/\S/.test(child.value)),
  );
  if (rest.length > 0 || code?.type !== 'element' || code.tagName !== 'code')
    return;
  const className = code.properties.className;
  if (!Array.isArray(className) || !className.includes('language-mermaid'))
    return;
  let text = '';
  visit(code, 'text', (node) => {
    text += node.value;
  });
  return text;
}

/** Replaces each mermaid fence with its SVG, drawn or from the cache. */
export function rehypeMermaid() {
  return async (tree: HastRoot, file: VFile) => {
    const found: {
      parent: Element | HastRoot;
      pre: Element;
      diagram: string;
    }[] = [];
    visit(tree, 'element', (node, _, parent) => {
      if (node.tagName !== 'pre' || !parent) return;
      const diagram = mermaidSource(node);
      if (diagram !== undefined) found.push({ parent, pre: node, diagram });
    });
    if (found.length === 0) return;
    const svgs = await drawn(
      found.map((f) => f.diagram),
      file,
    );
    found.forEach(({ parent, pre }, i) => {
      const svg = fromHtmlIsomorphic(svgs[i], { fragment: true })
        .children[0] as ElementContent;
      parent.children[parent.children.indexOf(pre)] = svg;
    });
  };
}
