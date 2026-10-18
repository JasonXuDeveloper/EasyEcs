/**
 * Adapters for the legacy pages: plain GitHub Markdown in content/*.md with no
 * frontmatter, readable on GitHub as they are. Until a page is rewritten as
 * `page.mdx` + `page.zh.mdx` (docs/WRITING.md), the site reads it like this:
 *
 * - title: the first H1; description: the first prose paragraph
 *   (`legacyFrontmatter`, used by the frontmatter schema in source.config.ts);
 * - the H1 and the "[Docs index](README.md) ·" breadcrumb under it are dropped
 *   (the page renders its own title, the sidebar replaces the breadcrumb);
 * - relative links to other pages become site URLs (`/en/docs/guide/queries`),
 *   links to files outside content/ (README, MIGRATION_GUIDE, benchmarks)
 *   become GitHub blob URLs.
 *
 * `remarkLegacyPage` only touches files without frontmatter, so MDX pages are
 * never rewritten. Build time only (imported by source.config.ts).
 */
import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { Link, Paragraph, Root } from 'mdast';
import { visit } from 'unist-util-visit';

const repoBlobUrl = 'https://github.com/JasonXuDeveloper/EasyEcs/blob/master';

/** Markdown inline syntax -> plain text, for titles and meta descriptions. */
function plain(markdown: string) {
  return markdown
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[`*_]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function clip(text: string, max: number) {
  if (text.length <= max) return text;
  const cut = text.lastIndexOf(' ', max - 1);
  return `${text.slice(0, cut > 0 ? cut : max - 1)}…`;
}

/** The blocks of a Markdown file (runs of lines between blank lines), code fences kept whole. */
function blocks(source: string) {
  const result: string[] = [];
  let current: string[] = [];
  let fenced = false;
  for (const line of source.split(/\r?\n/)) {
    if (line.startsWith('```')) fenced = !fenced;
    if (!fenced && line.trim() === '') {
      if (current.length > 0) result.push(current.join('\n'));
      current = [];
    } else {
      current.push(line);
    }
  }
  if (current.length > 0) result.push(current.join('\n'));
  return result;
}

const hasFrontmatter = (source: string) => /^---\r?\n/.test(source);

/** Title and description of a legacy page: its first H1 and first prose paragraph. */
export function legacyFrontmatter(source: string) {
  const all = blocks(source);
  const h1 = all.findIndex((b) => b.startsWith('# '));
  const title = h1 >= 0 ? plain(all[h1].slice(2)) : 'EasyEcs';
  const prose = all
    .slice(h1 + 1)
    .find(
      (b) =>
        !b.startsWith('[Docs index]') &&
        !/^(#|\||>|-|\*|\d+\.|```|<)/.test(b.trimStart()),
    );
  return {
    title,
    description: prose ? clip(plain(prose), 200) : undefined,
  };
}

const rawCache = new Map<string, boolean>();

function isLegacyFile(file: string) {
  let legacy = rawCache.get(file);
  if (legacy === undefined) {
    try {
      legacy = !hasFrontmatter(readFileSync(file, 'utf8'));
    } catch {
      legacy = false;
    }
    rawCache.set(file, legacy);
  }
  return legacy;
}

/** content/guide/queries.md -> /en/docs/guide/queries; README and index -> the folder */
export function contentFileUrl(relative: string) {
  const parts = relative.split(/[\\/]/);
  const name = (parts.pop() ?? '').replace(/(\.zh)?\.mdx?$/, '');
  const slugs =
    name === 'index' || name === 'README' ? parts : [...parts, name];
  return ['/en/docs', ...slugs].join('/');
}

function dropHeader(tree: Root) {
  const h1 = tree.children.findIndex(
    (n) => n.type === 'heading' && n.depth === 1,
  );
  if (h1 < 0) return;
  tree.children.splice(h1, 1);
  const next = tree.children[h1];
  if (next?.type !== 'paragraph') return;
  const paragraph = next as Paragraph;
  const [first, second] = paragraph.children;
  const label = first?.type === 'link' ? first.children[0] : undefined;
  if (label?.type !== 'text' || label.value !== 'Docs index') return;
  if (second?.type === 'text' && second.value.trimStart().startsWith('·')) {
    // "[Docs index](README.md) · rest of the line": keep the rest
    second.value = second.value.trimStart().slice(1).trimStart();
    paragraph.children.splice(0, 1);
    if (second.value === '' && paragraph.children.length === 1)
      tree.children.splice(h1, 1);
  } else {
    tree.children.splice(h1, 1);
  }
}

function rewriteLinks(tree: Root, file: string) {
  const contentRoot = path.resolve('content');
  const repoRoot = path.resolve('..');
  const dir = path.dirname(file);
  visit(tree, 'link', (node: Link) => {
    const url = node.url;
    if (/^[a-z]+:|^#|^\//i.test(url)) return;
    const at = url.indexOf('#');
    const hash = at >= 0 ? url.slice(at) : '';
    const target = path.resolve(dir, at >= 0 ? url.slice(0, at) : url);
    if (target.startsWith(contentRoot + path.sep) && /\.mdx?$/.test(target)) {
      node.url = `${contentFileUrl(path.relative(contentRoot, target))}${hash}`;
      return;
    }
    const relative = path.relative(repoRoot, target).split(path.sep).join('/');
    node.url = `${repoBlobUrl}/${relative}${hash}`;
  });
}

/** Remark plugin for legacy pages (files without frontmatter); MDX pages pass through. */
export function remarkLegacyPage() {
  return (tree: Root, file: { path?: string }) => {
    if (!file.path) return;
    const absolute = path.resolve(file.path);
    if (!isLegacyFile(absolute)) return;
    dropHeader(tree);
    rewriteLinks(tree, absolute);
  };
}
