/**
 * Mermaid diagrams are drawn at build time (rehype-mermaid, which runs
 * Mermaid in a headless Chromium through Playwright): the prerendered page
 * holds the SVG and the browser loads no Mermaid at all. Build time only
 * (imported by source.config.ts).
 */
import type { Code, Root } from 'mdast';
import type { MdxJsxFlowElement } from 'mdast-util-mdx-jsx';
import type { RehypeMermaidOptions } from 'rehype-mermaid';
import { visit } from 'unist-util-visit';

export const mermaidOptions: RehypeMermaidOptions = {
  strategy: 'inline-svg',
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
 * this puts the fence back inside it, for rehype-mermaid to replace with the
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
