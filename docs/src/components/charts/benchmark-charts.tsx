/**
 * The benchmark charts, built from src/lib/benchmarks.ts (src/data/bench.json).
 * Loaded lazily by ./index.tsx so visx is only downloaded on pages that show
 * a chart. EasyEcs is drawn in the accent, every other library in grey. Each
 * runtime that has numbers (.NET 10, .NET 11) gets its own bar in every row,
 * side by side, the later runtime in a lighter shade of the same colour; a
 * runtime with no number for a row has no bar there.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { comparisonTable, peerTable } from '@/components/bench-data';
import {
  type ComparisonQuery,
  comparison,
  comparisonLabel,
  formatBytes,
  formatRatio,
  formatTime,
  libraryName,
  measuredRuntimes,
  peerLabel,
  peers,
  primaryRuntime,
  type RuntimeId,
  sourceLine,
} from '@/lib/benchmarks';
import {
  featureComparison,
  featureOn,
  type Tier,
} from '@/lib/feature-comparison';
import type { Locale } from '@/lib/i18n';
import { docsVersion } from '@/lib/site';
import { BarChart, type ChartRow, type ChartSeries } from './bar-chart';

function useLang(lang?: Locale): Locale {
  const { locale } = useI18n();
  return lang ?? (locale === 'zh' ? 'zh' : 'en');
}

/** More than one runtime has numbers: each series splits into one bar per runtime */
const split = measuredRuntimes.length > 1;

/** The key of a series' bar on one runtime ("ours@net11.0"; the series' own key on one runtime) */
const at = (key: string, runtime: RuntimeId) =>
  split ? `${key}@${runtime}` : key;

/**
 * Each series as one series per runtime that has numbers: the same colour, a
 * lighter shade for the later runtime, the runtime in the label
 */
function perRuntime(series: ChartSeries[]): ChartSeries[] {
  if (!split) return series;
  return series.flatMap((s) =>
    measuredRuntimes.map((r, i) => ({
      key: at(s.key, r.id),
      label: `${s.label} (${r.label})`,
      color:
        i === 0 ? s.color : `color-mix(in oklab, ${s.color} 55%, transparent)`,
    })),
  );
}

function seriesFor(zh: boolean) {
  return {
    ours: { key: 'ours', label: 'EasyEcs', color: 'var(--ecs-chart-ours)' },
    other: {
      key: 'other',
      label: zh ? '其他库' : 'Other libraries',
      color: 'var(--ecs-chart-other)',
    },
  } satisfies Record<string, ChartSeries>;
}

export interface PeerChartProps {
  /** [Params] Entities of EcsComparisonBenchmark: "100000" (default) or "10000" */
  entities?: string;
  lang?: Locale;
  /** Set by ./index.tsx: finds the prerendered figure */
  chartId?: string;
}

/** The same server tick ported to EasyEcs, Friflo.Engine.ECS, Arch and DefaultEcs */
export function PeerChart({
  entities = '100000',
  lang,
  chartId,
}: PeerChartProps) {
  const zh = useLang(lang) === 'zh';
  const s = seriesFor(zh);
  const list = peers(entities);
  const on = measuredRuntimes.map((r) => ({ r, list: peers(entities, r.id) }));
  const count = Number(entities).toLocaleString('en-US');
  return (
    <BarChart
      id={chartId}
      title={
        zh
          ? `一个服务器 tick，${count} 个实体：EasyEcs 对比 Friflo、Arch、DefaultEcs`
          : `One server tick of ${count} entities: EasyEcs vs Friflo, Arch and DefaultEcs`
      }
      subtitle={
        zh
          ? '同一个模拟移植到每个库的最快写法，并在 setup 中校验结果一致。越短越快；标签为相对 EasyEcs 的耗时倍数和每个 tick 的分配。'
          : "The same simulation ported to each library's fast path, with identical results checked in setup. Shorter is faster; the badge is the time relative to EasyEcs and the memory allocated per tick."
      }
      series={perRuntime([s.ours, s.other])}
      rows={list.map((p) => ({
        label: peerLabel(p.method, docsVersion),
        // A bar per runtime that measured this library
        ...barsOf((r) => {
          const row = on
            .find((o) => o.r.id === r)
            ?.list.find((x) => x.method === p.method)?.row;
          return row
            ? { key: p.ours ? 'ours' : 'other', ns: row.meanNs }
            : undefined;
        }),
        badge: {
          text: `${p.ours || p.row.ratio === null ? '' : `${formatRatio(p.row.ratio)} · `}${formatBytes(p.row.allocatedBytes)}${zh ? ' / tick' : ' per tick'}`,
          tone: p.row.allocatedBytes === 0 ? 'win' : 'par',
        },
        detail:
          p.row.ratio !== null && !p.ours
            ? zh
              ? `耗时为 EasyEcs 的 ${formatRatio(p.row.ratio)}`
              : `${formatRatio(p.row.ratio)} the time of EasyEcs`
            : undefined,
      }))}
      axisLabel={zh ? '每个 tick 的耗时' : 'Time per tick'}
      tickFormat={formatTime}
      footnotes={[sourceLine(zh)]}
      table={{
        caption: zh ? '数据表' : 'Data table',
        ...peerTable(entities, zh, docsVersion),
      }}
    />
  );
}

export interface LeadChartProps {
  /** One tier of the comparisons (default "lead"), or "all" */
  tier?: Tier | 'all';
  /** Only the biggest leads */
  limit?: number | string;
  title?: string;
  subtitle?: string;
  lang?: Locale;
  /** Set by ./index.tsx: finds the prerendered figure */
  chartId?: string;
}

/**
 * The head-to-head comparisons as bars, the biggest lead first: each bar is
 * the fastest other library's time over EasyEcs' time, on a log axis (the
 * leads span from about 1× to over 100×).
 */
export function LeadChart({
  tier = 'lead',
  limit,
  title,
  subtitle,
  lang,
  chartId,
}: LeadChartProps) {
  const locale = useLang(lang);
  const zh = locale === 'zh';
  const s = seriesFor(zh);
  const items = featureComparison(tier === 'all' ? undefined : tier).slice(
    0,
    limit === undefined ? undefined : Number(limit),
  );
  const top = Math.max(...items.map((i) => i.ratio), 2);
  // 1×, 2×, 5×, 10×, 20×, 50×, ... up to the largest lead
  const ticks = [1, 2, 5, 10, 20, 50, 100, 200, 500].filter(
    (t) => t <= top * 1.05,
  );
  return (
    <BarChart
      id={chartId}
      title={
        title ??
        (zh
          ? '逐项对比：次快的库耗时是 EasyEcs 的几倍'
          : 'Feature by feature: how many times longer the next-fastest library takes')
      }
      subtitle={
        subtitle ??
        (zh
          ? '每一项都在 Friflo.Engine.ECS、Arch、DefaultEcs 里用各自惯用的写法做同样的事，并在 setup 中校验结果一致。条形为最快的其他库耗时除以 EasyEcs 耗时（对数坐标）。'
          : "Each comparison does the same work in Friflo.Engine.ECS, Arch and DefaultEcs, each library's own idiomatic way, with identical results checked in setup. A bar is the fastest other library's time over EasyEcs' time (log scale).")
      }
      series={perRuntime([s.ours])}
      rows={items.map((item) => {
        const next = item.versus;
        const bars = barsOf(
          (r) => {
            const same = r === primaryRuntime ? item : featureOn(item, r);
            return same ? { key: 'ours', ns: same.ratio } : undefined;
          },
          (v) => `${formatRatio(v)} · ${next.library}`,
        );
        return {
          label: item.label[locale],
          ...bars,
          detail: `EasyEcs ${formatTime(item.ours.meanNs)}, ${libraryName(next.library)} ${formatTime(next.row.meanNs)}`,
        };
      })}
      scale="log"
      tickValues={ticks}
      max={top}
      axisLabel={
        zh
          ? '它的耗时 ÷ EasyEcs 耗时（对数坐标）'
          : "Its time ÷ EasyEcs' time (log scale)"
      }
      tickFormat={(v) => `${v}×`}
      footnotes={[sourceLine(zh)]}
      table={{
        caption: zh ? '数据表' : 'Data table',
        head: zh
          ? ['测的是什么', 'EasyEcs', '次快的库', '对比 EasyEcs']
          : ['What is measured', 'EasyEcs', 'Next fastest', 'vs EasyEcs'],
        rows: items.map((item) => [
          item.label[locale],
          formatTime(item.ours.meanNs),
          `${libraryName(item.versus.library)} ${formatTime(item.versus.row.meanNs)}`,
          formatRatio(item.ratio),
        ]),
      }}
    />
  );
}

export interface BenchChartProps extends ComparisonQuery {
  /** Alias of `params` for a single value: param="Removed=1000" */
  param?: string;
  /** Defaults to the class name and the [Params] filter */
  title?: string;
  subtitle?: string;
  lang?: Locale;
  /** Set by ./index.tsx: finds the prerendered figure */
  chartId?: string;
}

/**
 * "baseline" for EasyEcs' own row, "2.54× the time of EasyEcs" for the others;
 * "0.29× the time of the baseline" in EasyEcs' own benchmarks
 */
function vsText(vs: number | null, ours: boolean, zh: boolean, own = false) {
  if (vs === null) return undefined;
  if (ours && Math.abs(vs - 1) < 1e-9) return zh ? '基准' : 'baseline';
  if (own)
    return zh
      ? `耗时为基准的 ${formatRatio(vs)}`
      : `${formatRatio(vs)} the time of the baseline`;
  return zh
    ? `耗时为 EasyEcs 的 ${formatRatio(vs)}`
    : `${formatRatio(vs)} the time of EasyEcs`;
}

/**
 * Any benchmark class as bars, EasyEcs in the accent and every other library
 * in grey: `<BenchChart bench="Features.RemovalComparison" param="Removed=1000" />`.
 * Each bar carries its time relative to EasyEcs and its allocations; classes
 * where every library runs the same cases (OperationBenchmark) get one group
 * per case with a bar per library.
 */
export function BenchChart({
  bench,
  params,
  param,
  methods,
  title,
  subtitle,
  lang,
  chartId,
}: BenchChartProps) {
  const zh = useLang(lang) === 'zh';
  const s = seriesFor(zh);
  const query = { bench, params: params ?? param, methods };
  const { rows, varying, grouped, own } = comparison(query);
  // The same rows on each runtime that has numbers
  const on = measuredRuntimes.map((r) => ({
    r: r.id,
    rows: comparison({ ...query, runtime: r.id }).rows,
  }));
  const rowKey = (row: { method: string; params: Record<string, string> }) =>
    `${row.method} ${JSON.stringify(row.params)}`;
  const sameOn = (runtime: RuntimeId, item: (typeof rows)[number]) =>
    on
      .find((o) => o.r === runtime)
      ?.rows.find((x) => rowKey(x.row) === rowKey(item.row));
  const heading =
    title ??
    `${bench.split('.').pop()}${query.params ? ` (${query.params})` : ''}`;
  const alloc = (bytes: number | null) =>
    `${formatBytes(bytes)}${zh ? ' 分配' : ' allocated'}`;

  const chartRows: ChartRow[] = [];
  // EasyEcs' own benchmarks have no other library: one series, no legend
  let series: ChartSeries[] = own ? [s.ours] : [s.ours, s.other];
  if (grouped) {
    const libraries = [...new Set(rows.map((r) => r.library))];
    series = libraries.map((library, i) =>
      library === 'EasyEcs'
        ? s.ours
        : {
            key: library,
            label: libraryName(library),
            color:
              i <= 1
                ? 'var(--ecs-chart-other)'
                : `color-mix(in oklab, var(--ecs-chart-other) ${100 - 25 * (i - 1)}%, transparent)`,
          },
    );
    const keyOf = (library: string) =>
      library === 'EasyEcs' ? 'ours' : library;
    for (const item of rows) {
      const label = comparisonLabel(item, varying, false);
      let row = chartRows.find((r) => r.label === label);
      if (!row) {
        row = { label, code: true, values: {}, text: {} };
        chartRows.push(row);
      }
      const key = keyOf(item.library);
      for (const { r } of on) {
        const same = sameOn(r, item);
        if (!same) continue;
        // Each bar: its time and its allocations
        row.values[at(key, r)] = same.row.meanNs;
        row.text[at(key, r)] =
          `${formatTime(same.row.meanNs)} · ${formatBytes(same.row.allocatedBytes)}`;
      }
      if (!item.ours && item.vsOurs !== null) {
        // The badge: each peer's time relative to EasyEcs for this case
        const ratio = `${libraryName(item.library)} ${formatRatio(item.vsOurs)}`;
        const slower = item.vsOurs >= 1;
        row.badge = row.badge
          ? {
              text: `${row.badge.text}, ${ratio}`,
              tone: row.badge.tone === 'win' && slower ? 'win' : 'par',
            }
          : { text: ratio, tone: slower ? 'win' : 'par' };
        row.detail = vsText(item.vsOurs, false, zh);
      }
    }
  } else {
    for (const item of rows) {
      chartRows.push({
        label: comparisonLabel(item, varying),
        ...barsOf((r) => {
          const same = sameOn(r, item);
          return same
            ? { key: item.ours ? 'ours' : 'other', ns: same.row.meanNs }
            : undefined;
        }),
        // The badge: time relative to EasyEcs' baseline and allocations
        badge: {
          text:
            item.vsOurs === null ||
            (item.ours && Math.abs(item.vsOurs - 1) < 1e-9)
              ? alloc(item.row.allocatedBytes)
              : `${formatRatio(item.vsOurs)} · ${alloc(item.row.allocatedBytes)}`,
          tone: item.row.allocatedBytes === 0 ? 'win' : 'par',
        },
        detail: vsText(item.vsOurs, item.ours, zh, item.own),
      });
    }
  }

  return (
    <BarChart
      id={chartId}
      title={heading}
      subtitle={subtitle}
      series={perRuntime(series)}
      rows={chartRows}
      axisLabel={zh ? '耗时（越短越快）' : 'Time (shorter is faster)'}
      tickFormat={formatTime}
      footnotes={[sourceLine(zh)]}
      table={{
        caption: zh ? '数据表' : 'Data table',
        ...comparisonTable(query, zh),
      }}
    />
  );
}

/**
 * A row's bars, one per runtime that has numbers: `bar` gives the series key
 * and the value on a runtime, or undefined where that runtime has no number
 * (no bar, never an estimate)
 */
function barsOf(
  bar: (runtime: RuntimeId) => { key: string; ns: number } | undefined,
  format: (value: number) => string = formatTime,
): Pick<ChartRow, 'values' | 'text'> {
  const values: ChartRow['values'] = {};
  const text: ChartRow['text'] = {};
  for (const r of measuredRuntimes) {
    const b = bar(r.id);
    if (!b) continue;
    values[at(b.key, r.id)] = b.ns;
    text[at(b.key, r.id)] = format(b.ns);
  }
  return { values, text };
}
