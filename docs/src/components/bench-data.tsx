/**
 * Benchmark numbers in prose and tables, for MDX (src/mdx-components.tsx) and
 * the Markdown copies (src/lib/markdown.tsx, `markdown` prop):
 *
 * - <BenchValue k="tick.oneWorld" />: one named number (keys in src/lib/benchmarks.ts)
 * - <BenchTable bench="Features.RemovalComparison" param="Removed=1000" />: a
 *   class (or the rows a filter picks), each row against EasyEcs
 * - <BenchSource />: the machine and tool line
 * - <FeatureComparison tier="lead" />: every head-to-head comparison (or one
 *   tier), the biggest lead first (src/lib/feature-comparison.ts)
 * - <FeatureCount k="ahead" />: how many comparisons EasyEcs leads (or `total`,
 *   `lead`, `close`, `behind`)
 *
 * Every value comes from src/data/bench.json; MDX never types a number.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { Count } from '@/components/count';
import {
  benchValue,
  type ComparisonQuery,
  comparison,
  formatBytes,
  formatRatio,
  formatTime,
  libraryName,
  sourceLine,
} from '@/lib/benchmarks';
import {
  type FeatureResult,
  featureComparison,
  featureCounts,
  type Tier,
} from '@/lib/feature-comparison';
import type { Locale } from '@/lib/i18n';
import { siteUrl } from '@/lib/site';

/** One named number; it counts up the first time it comes into view */
export function BenchValue({ k }: { k: string }) {
  return <Count duration={700}>{benchValue(k)}</Count>;
}

function useZh(lang?: Locale) {
  const { locale } = useI18n();
  return (lang ?? locale) === 'zh';
}

export function BenchSource({ lang }: { lang?: Locale }) {
  const zh = useZh(lang);
  return <p className="text-xs text-fd-muted-foreground">{sourceLine(zh)}</p>;
}

export interface BenchTableProps extends ComparisonQuery {
  /** Alias of `params` for a single value: param="Written=1000" */
  param?: string;
  lang?: Locale;
}

/**
 * A comparison as table cells: library, case, the [Params] that vary, mean,
 * time relative to EasyEcs and allocations. Shared by <BenchTable>, the data
 * table under <BenchChart> and the Markdown copies.
 */
export function comparisonTable(
  query: ComparisonQuery,
  zh: boolean,
): { head: string[]; rows: string[][] } {
  const { rows, varying, own } = comparison(query);
  const withCase = rows.some((r) => r.case !== '');
  // EasyEcs' own benchmarks: no library column, ratios against the baseline
  const head = [
    ...(own ? [] : [zh ? '库' : 'Library']),
    ...(withCase ? [zh ? '用例' : 'Case'] : []),
    ...varying,
    zh ? '平均耗时' : 'Mean',
    own
      ? zh
        ? '对比基准'
        : 'vs baseline'
      : zh
        ? '对比 EasyEcs'
        : 'vs EasyEcs',
    zh ? '分配' : 'Allocated',
  ];
  return {
    head,
    rows: rows.map((item) => [
      ...(own ? [] : [libraryName(item.library)]),
      ...(withCase ? [item.case || '—'] : []),
      ...varying.map((k) => item.row.params[k]),
      formatTime(item.row.meanNs),
      item.vsOurs === null ? '—' : formatRatio(item.vsOurs),
      formatBytes(item.row.allocatedBytes),
    ]),
  };
}

/** A whole benchmark class (or the rows a filter picks) as a table */
export function BenchTable(props: BenchTableProps) {
  return benchTable(props, useZh(props.lang), false);
}

/** The table without hooks; `markdown` gives the plain form of the Markdown copies */
export function benchTable(
  props: BenchTableProps,
  zh: boolean,
  markdown: boolean,
) {
  const { head, rows } = comparisonTable(
    { ...props, params: props.params ?? props.param },
    zh,
  );
  // Text columns (library, case, [Params]) left, the last three numbers right
  const numeric = (i: number) => i >= head.length - 3;
  const table = (
    <table>
      <thead>
        <tr>
          {head.map((cell, i) => (
            <th key={cell} className={numeric(i) ? 'text-end' : undefined}>
              {cell}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((cells, r) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: static table
          <tr key={r}>
            {cells.map((cell, i) => (
              <td
                // biome-ignore lint/suspicious/noArrayIndexKey: static table
                key={i}
                className={numeric(i) ? 'text-end tabular-nums' : undefined}
              >
                {cell}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
  if (markdown)
    return (
      <>
        {table}
        <p>{sourceLine(zh)}</p>
      </>
    );
  return (
    <figure className="my-6">
      <div className="overflow-x-auto">{table}</div>
      <figcaption className="mt-2 text-xs text-fd-muted-foreground">
        {sourceLine(zh)}
      </figcaption>
    </figure>
  );
}

/* ------------------------------------------------------------------ */
/* Feature by feature                                                  */
/* ------------------------------------------------------------------ */

/** "`[Changed<T>]` is ..." -> text with <code> for the backticked parts */
function inlineCode(text: string) {
  return text.split('`').map((part, i) =>
    // biome-ignore lint/suspicious/noArrayIndexKey: static text
    i % 2 ? <code key={i}>{part}</code> : part,
  );
}

/** A number and its unit kept on one line */
const nowrap = (text: string) => (
  <span className="whitespace-nowrap">{text}</span>
);

/** ", 4.00 MB allocated" for a row that allocates, else nothing */
function allocNote(bytes: number | null, zh: boolean) {
  if (!bytes) return null;
  return zh ? (
    <>，{nowrap(`分配 ${formatBytes(bytes)}`)}</>
  ) : (
    <>, {nowrap(`${formatBytes(bytes)} allocated`)}</>
  );
}

/** "Arch 1.21 ms" plus its allocations when it allocates */
function timed(row: FeatureResult['ours'], zh: boolean, name?: string) {
  return (
    <>
      {name ? `${name} ` : ''}
      {nowrap(formatTime(row.meanNs))}
      {allocNote(row.allocatedBytes, zh)}
    </>
  );
}

/** "15×", or "1.09×" over "(within error)" */
function ratioText(item: FeatureResult, zh: boolean) {
  return (
    <>
      {nowrap(formatRatio(item.ratio))}
      {item.withinError && (
        <>
          {' '}
          <span className="block text-xs font-normal text-fd-muted-foreground">
            {zh ? '（误差内）' : '(within error)'}
          </span>
        </>
      )}
    </>
  );
}

export interface FeatureComparisonProps {
  /** One tier: "lead" (a clear lead), "close" or "behind"; default: all, sorted */
  tier?: Tier;
  /** Against one library ("Friflo", "Arch", "DefaultEcs") instead of the fastest other one */
  library?: string;
  /** Only the first rows */
  limit?: number | string;
  lang?: Locale;
}

/**
 * The head-to-head comparisons as a table, the biggest lead first: what is
 * measured (linked to its section of the comparison page), EasyEcs' time, the
 * fastest other library (or `library`) and its time relative to EasyEcs, then
 * the other libraries; where EasyEcs is behind, why instead.
 */
export function FeatureComparison(props: FeatureComparisonProps) {
  return featureTable(props, useZh(props.lang), false);
}

/** The table without hooks; `markdown` gives the plain form of the Markdown copies */
export function featureTable(
  { tier, library, limit, lang }: FeatureComparisonProps,
  zh: boolean,
  markdown: boolean,
) {
  const items = featureComparison(tier, library).slice(
    0,
    limit === undefined ? undefined : Number(limit),
  );
  const behind = tier === 'behind';
  // The last column: why EasyEcs is behind (in a table against one library,
  // when it is behind in any row), the other libraries, or nothing
  const last =
    behind || (library && items.some((i) => i.tier === 'behind'))
      ? 'why'
      : library
        ? undefined
        : 'others';
  const locale: Locale = lang ?? (zh ? 'zh' : 'en');
  const href = (anchor: string) =>
    `${markdown ? siteUrl : ''}/${locale}/docs/comparison#${anchor}`;
  const versusHead = library
    ? libraryName(library)
    : zh
      ? behind
        ? '更快的库'
        : '次快的库'
      : behind
        ? 'Faster library'
        : 'Next fastest';
  const head = [
    zh ? '测的是什么' : 'What is measured',
    'EasyEcs',
    versusHead,
    zh ? '它的耗时对比 EasyEcs' : 'Its time vs EasyEcs',
    ...(last === 'why' ? [zh ? '原因' : 'Why'] : []),
    ...(last === 'others' ? [zh ? '其余的库' : 'The others'] : []),
  ];
  const others = (item: FeatureResult) => {
    const rest = item.rivals.filter((r) => r !== item.versus);
    if (!rest.length) return '—';
    return rest.map((r, i) => (
      <span key={r.library}>
        {i > 0 && (zh ? '；' : '; ')}
        {nowrap(`${libraryName(r.library)} ${formatRatio(r.vs)}`)}
        {allocNote(r.row.allocatedBytes, zh)}
      </span>
    ));
  };
  const table = (
    <table>
      <thead>
        <tr>
          {head.map((cell, i) => (
            <th
              key={cell}
              className={i === 1 || i === 3 ? 'text-end' : undefined}
            >
              {cell}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {items.map((item) => (
          <tr key={item.label.en}>
            <td className="min-w-48">
              <a href={href(item.anchor)}>{item.label[locale]}</a>
            </td>
            <td className="text-end tabular-nums">{timed(item.ours, zh)}</td>
            <td className="tabular-nums">
              {timed(
                item.versus.row,
                zh,
                library ? undefined : libraryName(item.versus.library),
              )}
            </td>
            <td className="text-end font-medium tabular-nums">
              {ratioText(item, zh)}
            </td>
            {last === 'why' && (
              <td className="min-w-72">
                {item.tier !== 'behind'
                  ? ''
                  : item.why
                    ? inlineCode(item.why[locale])
                    : '—'}
              </td>
            )}
            {last === 'others' && (
              <td className="tabular-nums">{others(item)}</td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  );
  if (markdown)
    return (
      <>
        {table}
        <p>{sourceLine(zh)}</p>
      </>
    );
  return (
    <figure className="my-6">
      <div className="overflow-x-auto">{table}</div>
      <figcaption className="mt-2 text-xs text-fd-muted-foreground">
        {sourceLine(zh)}
      </figcaption>
    </figure>
  );
}

type CountKey = keyof ReturnType<typeof featureCounts>;

/**
 * How many comparisons: `total`, `ahead` / `slower` (beyond the measurement
 * error), `even` (within it), or a tier (`lead`, `close`, `behind`); against
 * the fastest other library, or against `library`
 */
export function FeatureCount({
  k,
  library,
}: {
  k: CountKey;
  library?: string;
}) {
  return <Count duration={700}>{featureCounts(library)[k]}</Count>;
}
