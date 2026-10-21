import { execFileSync } from 'node:child_process';
import {
  rehypeCodeDefaultOptions,
  remarkImage,
  remarkMdxMermaid,
} from 'fumadocs-core/mdx-plugins';
import { remarkFeedbackBlock } from 'fumadocs-core/mdx-plugins/remark-feedback-block';
import {
  defineConfig,
  defineDocs,
  frontmatterSchema,
  metaSchema,
} from 'fumadocs-mdx/config';
import lastModified from 'fumadocs-mdx/plugins/last-modified';
import remarkCjkFriendly from 'remark-cjk-friendly';
import remarkCjkFriendlyGfmStrikethrough from 'remark-cjk-friendly-gfm-strikethrough';
import { z } from 'zod';
import { remarkCjkLineBreaks } from './src/lib/cjk';
import { legacyFrontmatter, remarkLegacyPage } from './src/lib/legacy';
import { rehypeMermaid, remarkMermaidSource } from './src/lib/mermaid';
import { remarkSearchSkip } from './src/lib/search-skip';

// "Last updated" dates come from `git log`, but only with the full history:
// in a shallow clone the oldest commit looks like it added every file, so
// most pages would get a wrong date. scripts/git-history.ts fetches the
// history before the build; if that failed (or there is no git), pages show
// no date rather than a wrong one.
function hasFullGitHistory() {
  try {
    return (
      execFileSync('git', ['rev-parse', '--is-shallow-repository'], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
      }).trim() === 'false'
    );
  } catch {
    return false;
  }
}

/** Set by vite.config.ts for `--mode scripts`. */
const contentOnly = process.env.DOCS_CONTENT_ONLY === '1';

type CodeTransformer = NonNullable<
  typeof rehypeCodeDefaultOptions.transformers
>[number];

/** A hast node, typed loosely: Shiki's hast types are another copy */
interface CodeNode {
  type: string;
  value?: string;
  children?: CodeNode[];
}

function textOf(node: CodeNode): string {
  if (node.type === 'text') return node.value ?? '';
  return node.type === 'element'
    ? (node.children ?? []).map(textOf).join('')
    : '';
}

/**
 * The scripts read only the text of a code block (its Markdown form), so in
 * `--mode scripts` a block is not highlighted: without `[!code ...]` notation
 * it is tokenized as plain text (the `language-*` class keeps its language),
 * and each line becomes one text node once the notation transformers (which
 * need the real tokens) are done. A page's compiled code shrinks to a
 * fraction, and so does the scripts' compile time.
 */
const plainCode: CodeTransformer = {
  name: 'easyecs:plain-code',
  enforce: 'post',
  preprocess(code, options) {
    if (!code.includes('[!code')) options.lang = 'text';
  },
  code(code) {
    for (const line of code.children) {
      if (line.type !== 'element') continue;
      const text = textOf(line);
      line.children = text ? [{ type: 'text', value: text }] : [];
    }
  },
};

/**
 * Frontmatter: Fumadocs' fields plus
 * - `seoTitle`: the <title> and social title when the page title alone is too
 *   generic for search results ("Overview"); the page heading keeps `title`.
 * - `navTitle`: the sidebar's name for the page when the title is too long
 *   for it ("VoxelServer" for "VoxelServer: a Minecraft-style server").
 * - `faq`: the page's questions (## headings ending in "?") become FAQPage
 *   structured data.
 * Typed as the base schema: the extended type makes tsc's inference of the
 * collection types explode (TS2589). Read the extra fields through
 * `DocExtras` (src/lib/source.ts).
 */
const docSchema = frontmatterSchema.extend({
  seoTitle: frontmatterSchema.shape.description,
  faq: frontmatterSchema.shape.full,
  navTitle: frontmatterSchema.shape.description,
}) as unknown as typeof frontmatterSchema;

// One collection for both languages: content/{page}.mdx (English) and
// {page}.zh.mdx (Chinese). Legacy pages (plain GitHub Markdown, no
// frontmatter) get their title and description from the file itself
// (src/lib/legacy.ts) until they are rewritten as MDX.
export const docs = defineDocs({
  dir: 'content',
  docs: {
    schema: ({ source }) =>
      z.preprocess(
        (data) =>
          data && typeof data === 'object' && 'title' in data
            ? data
            : { ...legacyFrontmatter(source), ...(data as object) },
        docSchema,
      ) as unknown as typeof frontmatterSchema,
    postprocess: {
      includeProcessedMarkdown: true,
    },
  },
  meta: {
    schema: metaSchema,
  },
});

export default defineConfig({
  plugins: [
    lastModified({
      versionControl: hasFullGitHistory() ? 'git' : async () => undefined,
    }),
  ],
  mdxOptions: {
    // `language-*` class on code blocks, so the Markdown copies keep the
    // language of each fence (src/lib/markdown.tsx).
    rehypeCodeOptions: {
      ...rehypeCodeDefaultOptions,
      addLanguageClass: true,
      transformers: contentOnly
        ? [...(rehypeCodeDefaultOptions.transformers ?? []), plainCode]
        : rehypeCodeDefaultOptions.transformers,
    },
    remarkPlugins: [
      // `**` and `~~` next to CJK punctuation: CommonMark's flanking rules
      // leave `**实体组件系统（ECS）**这种` unparsed (a closing `**` after `）`
      // followed by a letter). These micromark extensions must come after
      // remark-gfm, which Fumadocs' preset puts before the plugins listed here.
      remarkCjkFriendly,
      remarkCjkFriendlyGfmStrikethrough,
      // A source line break between two Chinese characters is no space
      // (src/lib/cjk.ts).
      remarkCjkLineBreaks,
      // Legacy pages only (no frontmatter): drop the H1 and the breadcrumb,
      // turn relative links into site or GitHub URLs.
      remarkLegacyPage,
      [remarkImage, { external: false, publicDir: 'public' }],
      // ```mermaid fences -> <Mermaid chart="...">fence</Mermaid>: the
      // attribute is the Markdown copy's source, rehypeMermaid below
      // replaces the fence inside with the SVG.
      remarkMdxMermaid,
      remarkMermaidSource,
      // Block-level feedback: a "Feedback" button on each paragraph
      remarkFeedbackBlock,
      // `{/* search: skip */}`: the next block stays out of the search index
      // (src/lib/search-skip.ts)
      remarkSearchSkip,
    ],
    // Diagrams are drawn at build time, before rehype-code highlights fences.
    // The scripts (vite.config.ts, `--mode scripts`) read the Markdown form of
    // a page, which keeps the diagram source: they skip the drawing.
    rehypePlugins: (plugins) =>
      contentOnly ? plugins : [rehypeMermaid, ...plugins],
  },
});
