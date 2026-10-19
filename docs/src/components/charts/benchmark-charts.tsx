/**
 * The benchmark charts, built from src/lib/benchmarks.ts (src/data/bench.json).
 * Loaded lazily by ./index.tsx so visx is only downloaded on pages that show
 * a chart. EasyEcs is drawn in the accent, every other library in grey.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { comparisonTable } from '@/components/bench-data';
import {
  type ComparisonQuery,
  comparison,
  comparisonLabel,
  formatBytes,
  formatRatio,
  formatTime,
  libraryName,
  peerLabel,
  peers,
  sourceLine,
} from '@/lib/benchmarks';
import { featureComparison, type Tier } from '@/lib/feature-comparison';
import type { Locale } from '@/lib/i18n';
import { docsVersion } from '@/lib/site';
import { BarChart, type ChartRow, type ChartSeries } from './bar-chart';

function useLang(lang?: Locale): Locale {
  const { locale } = useI18n();
  return lang ?? (locale === 'zh' ? 'zh' : 'en');
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
}

/** The same server tick ported to EasyEcs, Friflo.Engine.ECS, Arch and DefaultEcs */
export function PeerChart({ entities = '100000', lang }: PeerChartProps) {
  const zh = useLang(lang) === 'zh';
  const s = seriesFor(zh);
  const list = peers(entities);
  const count = Number(entities).toLocaleString('en-US');
  return (
    <BarChart
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
      series={[s.ours, s.other]}
      rows={list.map((p) => ({
        label: peerLabel(p.method, docsVersion),
        values: p.ours ? { ours: p.row.meanNs } : { other: p.row.meanNs },
        text: {
          ours: formatTime(p.row.meanNs),
          other: formatTime(p.row.meanNs),
        },
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
        head: zh
          ? ['库', '耗时', '对比 EasyEcs', '分配']
          : ['Library', 'Time', 'vs EasyEcs', 'Allocated'],
        rows: list.map((p) => [
          peerLabel(p.method, docsVersion),
          formatTime(p.row.meanNs),
          p.row.ratio === null ? '—' : formatRatio(p.row.ratio),
          formatBytes(p.row.allocatedBytes),
        ]),
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
      series={[s.ours]}
      rows={items.map((item) => {
        const next = item.versus;
        return {
          label: item.label[locale],
          values: { ours: item.ratio },
          text: { ours: `${formatRatio(item.ratio)} · ${next.library}` },
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
}: BenchChartProps) {
  const zh = useLang(lang) === 'zh';
  const s = seriesFor(zh);
  const query = { bench, params: params ?? param, methods };
  const { rows, varying, grouped, own } = comparison(query);
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
      row.values[key] = item.row.meanNs;
      // Each bar: its time and its allocations
      row.text[key] =
        `${formatTime(item.row.meanNs)} · ${formatBytes(item.row.allocatedBytes)}`;
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
        values: item.ours
          ? { ours: item.row.meanNs }
          : { other: item.row.meanNs },
        text: {
          ours: formatTime(item.row.meanNs),
          other: formatTime(item.row.meanNs),
        },
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
      title={heading}
      subtitle={subtitle}
      series={series}
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
