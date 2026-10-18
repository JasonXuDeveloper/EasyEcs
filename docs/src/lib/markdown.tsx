/**
 * Renders a docs page to clean Markdown for LLMs and the `.md` copies: the
 * compiled MDX runs with Markdown versions of every component (Fumadocs'
 * `renderToMarkdown`), so tabs, callouts, cards and the benchmark tables come
 * out as plain Markdown instead of JSX. Build-time only (scripts/).
 */
import { asMarkdown, md, renderToMarkdown } from 'fumadocs-core/server';
import type { ComponentProps, ReactNode } from 'react';
import { benchTable } from '@/components/bench-data';
import {
  benchValue,
  formatBytes,
  formatRatio,
  formatTime,
  peerLabel,
  peers,
  sourceLine,
} from '@/lib/benchmarks';
import { getMDXComponents } from '@/mdx-components';
import type { Locale } from './i18n';
import { docsVersion, siteUrl } from './site';
import type { source } from './source';

type Page = NonNullable<ReturnType<typeof source.getPage>>;

interface Children {
  children?: ReactNode;
}

const calloutLabels: Record<Locale, Record<string, string>> = {
  en: { info: 'Note', idea: 'Tip', warn: 'Warning', error: 'Important' },
  zh: { info: '注意', idea: '提示', warn: '警告', error: '重要' },
};

/** Site links become absolute, `#anchor` links point at the page itself. */
function absolute(href: string, pageUrl: string) {
  if (href.startsWith('#')) return `${siteUrl}${pageUrl}${href}`;
  return href.startsWith('/') ? `${siteUrl}${href}` : href;
}

function table(head: ReactNode[], rows: ReactNode[][]) {
  return (
    <table>
      <thead>
        <tr>
          {head.map((cell, i) => (
            // biome-ignore lint/suspicious/noArrayIndexKey: static table
            <th key={i}>{cell}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((cells, r) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: static table
          <tr key={r}>
            {cells.map((cell, i) => (
              // biome-ignore lint/suspicious/noArrayIndexKey: static table
              <td key={i}>{cell}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function markdownComponents(lang: Locale, pageUrl: string) {
  const link = (href: string) => absolute(href, pageUrl);
  const zh = lang === 'zh';
  return {
    a({ href, children }: ComponentProps<'a'>) {
      asMarkdown();
      if (!href) return children;
      return md`[${children}](${link(href)})`;
    },
    FeedbackBlock({ children }: Children) {
      asMarkdown();
      return children;
    },
    AnchorRedirect() {
      asMarkdown();
      return null;
    },
    Callout({
      type = 'info',
      title,
      children,
    }: Children & { type?: string; title?: ReactNode }) {
      asMarkdown();
      return (async () => {
        const label =
          title ?? calloutLabels[lang][type] ?? calloutLabels[lang].info;
        const body = (await md`${children}`).replace(/\n{3,}/g, '\n\n').trim();
        return md.linePrefix('> ')`**${label}**\n\n${body}`;
      })();
    },
    Tabs({ children }: Children) {
      asMarkdown();
      return children;
    },
    Tab({
      value,
      title,
      children,
    }: Children & { value?: string; title?: string }) {
      asMarkdown();
      return md`\n**${title ?? value ?? ''}**\n\n${children}\n\n`;
    },
    Cards({ children }: Children) {
      asMarkdown();
      return md`\n${children}\n\n`;
    },
    Card({
      title,
      href,
      description,
      children,
    }: Children & {
      title?: ReactNode;
      href?: string;
      description?: ReactNode;
    }) {
      asMarkdown();
      const name = href ? md`[${title}](${link(href)})` : title;
      return (async () => {
        const text = (await md`${description ?? children}`)
          .trim()
          .replace(/\s*\n\s*/g, ' ');
        return text ? md`- ${name}: ${text}\n` : md`- ${name}\n`;
      })();
    },
    Steps({ children }: Children) {
      asMarkdown();
      return children;
    },
    Step({ children }: Children) {
      asMarkdown();
      return children;
    },
    Accordions({ children }: Children) {
      asMarkdown();
      return children;
    },
    Accordion({ title, children }: Children & { title?: ReactNode }) {
      asMarkdown();
      return md`\n**${title}**\n\n${children}\n\n`;
    },
    TypeTable({
      type,
    }: {
      type: Record<
        string,
        { type?: ReactNode; description?: ReactNode; default?: ReactNode }
      >;
    }) {
      asMarkdown();
      const entries = Object.entries(type);
      const withDefault = entries.some(([, v]) => v.default !== undefined);
      const head = zh
        ? ['名称', '类型', '说明', ...(withDefault ? ['默认值'] : [])]
        : ['Name', 'Type', 'Description', ...(withDefault ? ['Default'] : [])];
      return table(
        head,
        entries.map(([name, v]) => [
          <code key="n">{name}</code>,
          v.type ? <code key="t">{v.type}</code> : '',
          v.description ?? '',
          ...(withDefault
            ? [v.default !== undefined ? <code key="d">{v.default}</code> : '']
            : []),
        ]),
      );
    },
    Files({ children }: Children) {
      asMarkdown();
      return md`\n${children}\n\n`;
    },
    Folder({ name, children }: Children & { name: string }) {
      asMarkdown();
      return md`- ${name}/\n${md.indent(2)`${children}`}`;
    },
    File({ name }: { name: string }) {
      asMarkdown();
      return `- ${name}\n`;
    },
    Mermaid({ chart }: { chart: string }) {
      asMarkdown();
      return md`\n\`\`\`mermaid\n${chart.trim()}\n\`\`\`\n\n`;
    },
    BenchValue({ k }: { k: string }) {
      asMarkdown();
      return benchValue(k);
    },
    BenchSource() {
      asMarkdown();
      return sourceLine(zh);
    },
    BenchTable(props: Parameters<typeof benchTable>[0]) {
      asMarkdown();
      return benchTable(props, zh, true);
    },
    // A chart becomes its title, its data table and the source line.
    PeerChart({ entities = '100000' }: { entities?: string }) {
      asMarkdown();
      const list = peers(entities);
      return (
        <div>
          <p>
            <strong>
              {zh
                ? `一个服务器 tick，${Number(entities).toLocaleString('en-US')} 个实体`
                : `One server tick of ${Number(entities).toLocaleString('en-US')} entities`}
            </strong>
          </p>
          {table(
            zh
              ? ['库', '耗时', '对比 EasyEcs', '分配']
              : ['Library', 'Time', 'vs EasyEcs', 'Allocated'],
            list.map((p) => [
              peerLabel(p.method, docsVersion),
              formatTime(p.row.meanNs),
              p.row.ratio === null ? '—' : formatRatio(p.row.ratio),
              formatBytes(p.row.allocatedBytes),
            ]),
          )}
          <p>{sourceLine(zh)}</p>
        </div>
      );
    },
    BenchChart({
      title,
      ...query
    }: Parameters<typeof benchTable>[0] & { title?: string }) {
      asMarkdown();
      return (
        <div>
          {title && (
            <p>
              <strong>{title}</strong>
            </p>
          )}
          {benchTable(query, zh, true)}
        </div>
      );
    },
  };
}

/** The page body as Markdown, without the title. */
export async function pageBodyMarkdown(page: Page, lang: Locale) {
  const Body = page.data.body as (props: {
    components: Record<string, unknown>;
  }) => ReactNode;
  // Call the MDX content directly: its elements are then rendered with the
  // Markdown components above, and plain HTML elements become Markdown.
  return renderToMarkdown(
    Body({ components: withFallbacks(markdownComponents(lang, page.url)) }),
  );
}

/**
 * Any other component available to MDX (src/mdx-components.tsx) keeps just
 * its children, so a new component never breaks the build; give it a real
 * Markdown form above when it carries content.
 */
function withFallbacks(components: Record<string, unknown>) {
  const all: Record<string, unknown> = {};
  for (const name of Object.keys(getMDXComponents())) {
    if (!/^[A-Z]/.test(name)) continue;
    all[name] = ({ children }: Children) => {
      asMarkdown();
      return children ?? null;
    };
  }
  return { ...all, ...components };
}

/** Markdown inline syntax to plain text, for JSON-LD */
function plainText(markdown: string) {
  return markdown
    .replace(/```[\s\S]*?```/g, '')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/^\s*>\s?/gm, '')
    .replace(/^\s*[-*]\s+/gm, '')
    .replace(/\s*\n\s*/g, ' ')
    .trim();
}

/**
 * Question and answer pairs of an FAQ page: every `##` heading that ends with
 * a question mark, and the text under it.
 */
export function faqFromMarkdown(markdown: string) {
  const faq: { question: string; answer: string }[] = [];
  for (const section of markdown.split(/^## /m).slice(1)) {
    const [heading, ...body] = section.split('\n');
    const question = heading.trim();
    if (!/[?？]$/.test(question)) continue;
    const answer = plainText(body.join('\n').split(/^#{1,3} /m)[0]);
    if (answer) faq.push({ question, answer });
  }
  return faq;
}
