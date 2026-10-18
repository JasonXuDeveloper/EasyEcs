/**
 * Benchmark numbers in prose and tables, for MDX (src/mdx-components.tsx) and
 * the Markdown copies (src/lib/markdown.tsx, `markdown` prop):
 *
 * - <BenchValue k="tick.oneWorld" />: one named number (keys in src/lib/benchmarks.ts)
 * - <BenchTable bench="Features.RemovalComparison" param="Removed=1000" />: a
 *   class (or the rows a filter picks), each row against EasyEcs
 * - <BenchSource />: the machine and tool line
 *
 * Every value comes from src/data/bench.json; MDX never types a number.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
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
import type { Locale } from '@/lib/i18n';

export function BenchValue({ k }: { k: string }) {
  return <span className="tabular-nums">{benchValue(k)}</span>;
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
  const { rows, varying } = comparison(query);
  const withCase = rows.some((r) => r.case !== '');
  const head = [
    zh ? '库' : 'Library',
    ...(withCase ? [zh ? '用例' : 'Case'] : []),
    ...varying,
    zh ? '平均耗时' : 'Mean',
    zh ? '对比 EasyEcs' : 'vs EasyEcs',
    zh ? '分配' : 'Allocated',
  ];
  return {
    head,
    rows: rows.map((item) => [
      libraryName(item.library),
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
