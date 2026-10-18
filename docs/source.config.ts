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
import { z } from 'zod';
import { legacyFrontmatter, remarkLegacyPage } from './src/lib/legacy';

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

/**
 * Frontmatter: Fumadocs' fields plus
 * - `seoTitle`: the <title> and social title when the page title alone is too
 *   generic for search results ("Overview"); the page heading keeps `title`.
 * - `faq`: the page's questions (## headings ending in "?") become FAQPage
 *   structured data.
 * Typed as the base schema: the extended type makes tsc's inference of the
 * collection types explode (TS2589). Read the extra fields through
 * `DocExtras` (src/lib/source.ts).
 */
const docSchema = frontmatterSchema.extend({
  seoTitle: frontmatterSchema.shape.description,
  faq: frontmatterSchema.shape.full,
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
    rehypeCodeOptions: { ...rehypeCodeDefaultOptions, addLanguageClass: true },
    remarkPlugins: [
      // Legacy pages only (no frontmatter): drop the H1 and the breadcrumb,
      // turn relative links into site or GitHub URLs.
      remarkLegacyPage,
      [remarkImage, { external: false, publicDir: 'public' }],
      // ```mermaid fences -> <Mermaid chart="..." />
      remarkMdxMermaid,
      // Block-level feedback: a "Feedback" button on each paragraph
      remarkFeedbackBlock,
    ],
  },
});
