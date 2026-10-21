/**
 * Mermaid diagrams are drawn at build time (mermaid-isomorphic, which runs
 * Mermaid in a headless Chromium through Playwright): the prerendered page
 * holds the SVG and the browser loads no Mermaid at all. Build time only
 * (imported by source.config.ts).
 */
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type { Element, ElementContent, Root as HastRoot } from 'hast';
import { fromHtmlIsomorphic } from 'hast-util-from-html-isomorphic';
import type { Code, Root } from 'mdast';
import type { MdxJsxFlowElement } from 'mdast-util-mdx-jsx';
import {
  type CreateMermaidRendererOptions,
  createMermaidRenderer,
  type RenderOptions,
} from 'mermaid-isomorphic';
import { visit } from 'unist-util-visit';
import type { VFile } from 'vfile';

const renderOptions: RenderOptions = {
  // One drawing for both themes: `neutral` is grayscale, and the dark site
  // shows it inverted (src/components/mermaid.tsx).
  mermaidConfig: {
    theme: 'neutral',
    securityLevel: 'strict',
    // The site's text font (Tailwind's --font-sans): labels are measured
    // with the build's fonts and shown with the reader's.
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', 'Noto Sans', Arial, sans-serif",
  },
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
 * Drawing is the slow part of the build: a Chromium page per MDX file, and
 * `react-router build` compiles every page twice (client and server
 * environments). The SVGs of a page are cached by the page's diagrams and
 * the Mermaid version, in memory (the second environment) and on disk
 * (node_modules/.cache/mermaid, the next build). A page is the unit because
 * Mermaid numbers the SVG ids of one render (mermaid-0, mermaid-1, ...).
 */
const mermaidVersion: string = JSON.parse(
  fs.readFileSync(path.resolve('node_modules/mermaid/package.json'), 'utf8'),
).version;
const cacheDir = path.resolve('node_modules/.cache/mermaid');
const memory = new Map<string, Promise<string[]>>();
let renderer: ReturnType<typeof createMermaidRenderer> | undefined;

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

async function draw(diagrams: string[], key: string, file: VFile) {
  renderer ??= createMermaidRenderer({} satisfies CreateMermaidRendererOptions);
  const results = await renderer(diagrams, renderOptions);
  const svgs = results.map((result) => {
    if (result.status === 'fulfilled') return result.value.svg;
    const message = file.message(result.reason, { source: 'mermaid' });
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
