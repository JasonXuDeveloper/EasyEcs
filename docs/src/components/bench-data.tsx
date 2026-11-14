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
 * Every value comes from src/data/bench.json; MDX never types a number. The
 * runtimes (.NET 10, .NET 11) sit side by side: a column each in the tables,
 * both values in a named number once both have it; a runtime with no number
 * yet is an empty cell ("—"), never a guess.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { Count } from '@/components/count';
import {
  type BenchRow,
  benchValues,
  type ComparisonQuery,
  type ComparisonRow,
  comparison,
  formatBytes,
  formatRatio,
  formatTime,
  libraryName,
  peerLabel,
  peers,
  primaryRuntime,
  runtimes,
  sourceLine,
} from '@/lib/benchmarks';
import {
  type FeatureResult,
  featureComparison,
  featureCounts,
  featureOn,
  type Tier,
} from '@/lib/feature-comparison';
import type { Locale } from '@/lib/i18n';
import { siteUrl } from '@/lib/site';

/**
 * One named number, on every runtime that has it ("1.20 ms (.NET 10) · 1.10 ms
 * (.NET 11)"); it counts up the first time it comes into view
 */
export function BenchValue({ k, lang }: { k: string; lang?: Locale }) {
  return <Count duration={700}>{benchValues(k, useZh(lang))}</Count>;
}

function useZh(lang?: Locale) {
  const { locale } = useI18n();
  return (lang ?? locale) === 'zh';
}

/** "Mean (.NET 10)" / "平均耗时（.NET 10）" */
const perRuntime = (head: string, label: string, zh: boolean) =>
  zh ? `${head}（${label}）` : `${head} (${label})`;

const rowKey = (row: BenchRow) => `${row.method} ${JSON.stringify(row.params)}`;

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
 * A comparison as table cells: library, case, the [Params] that vary, then
 * mean, time relative to EasyEcs and allocations, each a column per runtime
 * (an empty cell where a runtime has no number). The rows and their order are
 * the primary runtime's, then any row only another runtime has. Shared by
 * <BenchTable>, the data table under <BenchChart> and the Markdown copies.
 */
export function comparisonTable(
  query: ComparisonQuery,
  zh: boolean,
): { head: string[]; rows: string[][]; numeric: number } {
  const per = runtimes.map((r) => ({
    runtime: r,
    result: comparison({ ...query, runtime: r.id }),
  }));
  const primary =
    per.find((p) => p.runtime.id === primaryRuntime)?.result ?? per[0].result;
  const shape = primary.rows.length
    ? primary
    : (per.find((p) => p.result.rows.length)?.result ?? primary);
  const { varying, own } = shape;
  const items: ComparisonRow[] = [...shape.rows];
  for (const { result } of per)
    for (const item of result.rows)
      if (!items.some((i) => rowKey(i.row) === rowKey(item.row)))
        items.push(item);
  const withCase = items.some((r) => r.case !== '');
  const metrics: [string, (item: ComparisonRow) => string][] = [
    [zh ? '平均耗时' : 'Mean', (i) => formatTime(i.row.meanNs)],
    [
      own
        ? zh
          ? '对比基准'
          : 'vs baseline'
        : zh
          ? '对比 EasyEcs'
          : 'vs EasyEcs',
      (i) => (i.vsOurs === null ? '—' : formatRatio(i.vsOurs)),
    ],
    [zh ? '分配' : 'Allocated', (i) => formatBytes(i.row.allocatedBytes)],
  ];
  // EasyEcs' own benchmarks: no library column, ratios against the baseline
  const head = [
    ...(own ? [] : [zh ? '库' : 'Library']),
    ...(withCase ? [zh ? '用例' : 'Case'] : []),
    ...varying,
    ...metrics.flatMap(([name]) =>
      per.map((p) => perRuntime(name, p.runtime.label, zh)),
    ),
  ];
  return {
    head,
    numeric: metrics.length * per.length,
    rows: items.map((item) => [
      ...(own ? [] : [libraryName(item.library)]),
      ...(withCase ? [item.case || '—'] : []),
      ...varying.map((k) => item.row.params[k] ?? '—'),
      ...metrics.flatMap(([, cell]) =>
        per.map((p) => {
          const same = p.result.rows.find(
            (r) => rowKey(r.row) === rowKey(item.row),
          );
          return same ? cell(same) : '—';
        }),
      ),
    ]),
  };
}

/**
 * The headline comparison (the same server tick in each library) as table
 * cells: library, then time, time relative to EasyEcs and allocations per
 * runtime. Shared by <PeerChart>'s data table and the Markdown copies.
 */
export function peerTable(
  entities: string,
  zh: boolean,
  version: string,
): { head: string[]; rows: string[][] } {
  const per = runtimes.map((r) => ({
    runtime: r,
    list: peers(entities, r.id),
  }));
  const order = (
    per.find((p) => p.runtime.id === primaryRuntime && p.list.length) ??
    per.find((p) => p.list.length) ??
    per[0]
  ).list.map((p) => p.method);
  for (const { list } of per)
    for (const p of list) if (!order.includes(p.method)) order.push(p.method);
  const metrics: [string, (row: BenchRow) => string][] = [
    [zh ? '耗时' : 'Time', (r) => formatTime(r.meanNs)],
    [
      zh ? '对比 EasyEcs' : 'vs EasyEcs',
      (r) => (r.ratio === null ? '—' : formatRatio(r.ratio)),
    ],
    [zh ? '分配' : 'Allocated', (r) => formatBytes(r.allocatedBytes)],
  ];
  return {
    head: [
      zh ? '库' : 'Library',
      ...metrics.flatMap(([name]) =>
        per.map((p) => perRuntime(name, p.runtime.label, zh)),
      ),
    ],
    rows: order.map((method) => [
      peerLabel(method, version),
      ...metrics.flatMap(([, cell]) =>
        per.map((p) => {
          const row = p.list.find((x) => x.method === method)?.row;
          return row ? cell(row) : '—';
        }),
      ),
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
  const {
    head,
    rows,
    numeric: numbers,
  } = comparisonTable({ ...props, params: props.params ?? props.param }, zh);
  // Text columns (library, case, [Params]) left, the numbers right
  const numeric = (i: number) => i >= head.length - numbers;
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
  // The order, tiers and the library compared against are the primary
  // runtime's; each other runtime adds EasyEcs' time and the ratio there
  const primaryLabel =
    runtimes.find((r) => r.id === primaryRuntime)?.label ?? primaryRuntime;
  const others_ = runtimes.filter((r) => r.id !== primaryRuntime);
  const ratioHead = zh ? '它的耗时对比 EasyEcs' : 'Its time vs EasyEcs';
  const head = [
    zh ? '测的是什么' : 'What is measured',
    perRuntime('EasyEcs', primaryLabel, zh),
    versusHead,
    perRuntime(ratioHead, primaryLabel, zh),
    ...others_.flatMap((r) => [
      perRuntime('EasyEcs', r.label, zh),
      perRuntime(ratioHead, r.label, zh),
    ]),
    ...(last === 'why' ? [zh ? '原因' : 'Why'] : []),
    ...(last === 'others' ? [zh ? '其余的库' : 'The others'] : []),
  ];
  const right = (i: number) =>
    i === 1 || i === 3 || (i > 3 && i < 4 + others_.length * 2);
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
            <th key={cell} className={right(i) ? 'text-end' : undefined}>
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
            {others_.map((r) => {
              const same = featureOn(item, r.id);
              return [
                <td key={`${r.id}-ours`} className="text-end tabular-nums">
                  {same ? timed(same.ours, zh) : '—'}
                </td>,
                <td
                  key={`${r.id}-ratio`}
                  className="text-end font-medium tabular-nums"
                >
                  {same ? ratioText(same, zh) : '—'}
                </td>,
              ];
            })}
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
  lang,
}: {
  k: CountKey;
  library?: string;
  lang?: Locale;
}) {
  return (
    <Count duration={700}>{featureCountText(k, library, useZh(lang))}</Count>
  );
}

/**
 * A count on every runtime that has comparisons: "38" when one has, "38
 * (.NET 10) · 40 (.NET 11)" when both have; shared with the Markdown copies
 */
export function featureCountText(
  k: CountKey,
  library: string | undefined,
  zh: boolean,
) {
  const found = runtimes.flatMap((r) => {
    const counts = featureCounts(library, r.id);
    return counts.total ? [{ label: r.label, n: counts[k] }] : [];
  });
  if (found.length === 0) return '—';
  if (found.length === 1) return String(found[0].n);
  return found
    .map((f) => (zh ? `${f.n}（${f.label}）` : `${f.n} (${f.label})`))
    .join(' · ');
}
