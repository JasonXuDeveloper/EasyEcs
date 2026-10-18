/**
 * Horizontal grouped bar chart drawn with visx (SVG, no canvas).
 *
 * - Prerenders: the SVG is complete in the static HTML at a default width and
 *   re-lays out to the container width after hydration.
 * - Colours are CSS variables (app.css), so light and dark mode need no JS.
 * - Every row is focusable and has a tooltip; a data table sits under the
 *   chart in <details> for screen readers and copy-paste.
 * - Bars grow in when scrolled into view, except with prefers-reduced-motion.
 */
import { AxisBottom } from '@visx/axis';
import { GridColumns } from '@visx/grid';
import { Group } from '@visx/group';
import { scaleLinear } from '@visx/scale';
import { BarRounded } from '@visx/shape';
import { type ReactNode, useEffect, useRef, useState } from 'react';
import { useReveal } from '@/components/showcase';
import { cn } from '@/lib/cn';

export interface ChartSeries {
  key: string;
  label: string;
  /** CSS colour, usually a var(--ecs-chart-*) token */
  color: string;
}

export interface ChartRow {
  label: string;
  /** Render the label in monospace (type names) */
  code?: boolean;
  /** Footnote marker appended to the label */
  mark?: string;
  /** Plotted values per series key; undefined skips the bar */
  values: Record<string, number | undefined>;
  /** Text at the tip of each bar and in the tooltip, per series key */
  text: Record<string, string>;
  badge?: { text: string; tone: 'win' | 'par' | 'loss' };
  /** Extra line in the tooltip */
  detail?: string;
}

export interface BarChartProps {
  title: string;
  subtitle?: ReactNode;
  series: ChartSeries[];
  rows: ChartRow[];
  axisLabel: string;
  tickFormat?: (value: number) => string;
  /** Upper end of the axis; defaults to the largest value */
  max?: number;
  footnotes?: ReactNode[];
  table?: { caption: string; head: string[]; rows: ReactNode[][] };
}

const BAR = 12;
const BAR_GAP = 4;
const HEADER = 20;
const BADGE_H = 18;
const ROW_GAP = 20;
const AXIS_BAND = 44;
const FALLBACK_WIDTH = 640;

/** Rough text width; CJK characters are about 1em wide. */
function textWidth(text: string, size: number, mono = false) {
  let width = 0;
  for (const ch of text) {
    if (/[⺀-鿿＀-￯]/.test(ch)) width += size;
    else width += size * (mono ? 0.62 : 0.56);
  }
  return width;
}

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(FALLBACK_WIDTH);
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const update = () =>
      setWidth(Math.max(Math.round(element.clientWidth), 260));
    update();
    if (!('ResizeObserver' in window)) return;
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

interface Layout {
  top: number;
  height: number;
  badgeBelow: boolean;
}

const badgeTone = {
  win: {
    fill: 'color-mix(in oklab, var(--color-fd-primary) 12%, transparent)',
    text: 'var(--color-fd-primary)',
  },
  par: {
    fill: 'var(--color-fd-muted)',
    text: 'var(--color-fd-muted-foreground)',
  },
  loss: { fill: 'var(--color-fd-muted)', text: 'var(--color-fd-foreground)' },
};

export function BarChart({
  title,
  subtitle,
  series,
  rows,
  axisLabel,
  tickFormat = (value) => String(value),
  max,
  footnotes,
  table,
}: BarChartProps) {
  const [containerRef, width] = useWidth<HTMLDivElement>();
  const [revealRef, shown] = useReveal<SVGSVGElement>();
  const [active, setActive] = useState<{
    index: number;
    x: number;
    y: number;
  } | null>(null);

  const valueGutter =
    Math.max(
      40,
      ...rows.flatMap((row) =>
        series.map((s) =>
          row.text[s.key] ? textWidth(row.text[s.key], 11) : 0,
        ),
      ),
    ) + 10;
  const plotWidth = Math.max(width - valueGutter, 80);
  const domainMax =
    max ??
    Math.max(
      ...rows.flatMap((row) => series.map((s) => row.values[s.key] ?? 0)),
    );
  const x = scaleLinear<number>({
    domain: [0, domainMax],
    range: [0, plotWidth],
    nice: max === undefined,
  });

  // Vertical layout of each row: label (+ badge), then one bar per series.
  const barCount = (row: ChartRow) =>
    series.filter((s) => row.values[s.key] !== undefined).length;
  const layouts: Layout[] = [];
  let y = 0;
  for (const row of rows) {
    const labelWidth = textWidth(row.label + (row.mark ?? ''), 13, row.code);
    const badgeWidth = row.badge ? textWidth(row.badge.text, 11) + 16 : 0;
    const badgeBelow = !!row.badge && labelWidth + 12 + badgeWidth > width;
    const header = HEADER + (badgeBelow ? BADGE_H + 4 : 0);
    const n = barCount(row);
    const height = header + 6 + n * BAR + (n - 1) * BAR_GAP;
    layouts.push({ top: y, height, badgeBelow });
    y += height + ROW_GAP;
  }
  const plotBottom = y - ROW_GAP + 10;
  const height = plotBottom + AXIS_BAND;
  const numTicks = width < 480 ? 4 : 6;

  const activeRow = active ? rows[active.index] : undefined;

  return (
    <figure className="not-prose my-6 rounded-xl border bg-fd-card p-4 sm:p-5">
      <figcaption className="mb-4">
        <p className="text-sm font-semibold text-fd-foreground">{title}</p>
        {subtitle && (
          <p className="mt-0.5 text-xs text-fd-muted-foreground">{subtitle}</p>
        )}
      </figcaption>
      {series.length > 1 && (
        <ul className="mb-4 flex flex-wrap gap-x-4 gap-y-1 text-xs text-fd-muted-foreground">
          {series.map((s) => (
            <li key={s.key} className="inline-flex items-center gap-1.5">
              <span
                className="inline-block size-2.5 rounded-sm"
                style={{ background: s.color }}
                aria-hidden="true"
              />
              {s.label}
            </li>
          ))}
        </ul>
      )}
      <div ref={containerRef} className="relative">
        {/* biome-ignore lint/a11y/useSemanticElements: <fieldset> cannot live in SVG; the group names the chart */}
        <svg
          ref={revealRef}
          className="ecs-chart block h-auto w-full overflow-visible"
          data-hidden={shown ? undefined : ''}
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          role="group"
          aria-label={title}
        >
          <GridColumns
            scale={x}
            height={plotBottom}
            numTicks={numTicks}
            stroke="var(--color-fd-border)"
            strokeWidth={1}
          />
          {rows.map((row, index) => {
            const layout = layouts[index];
            const header = HEADER + (layout.badgeBelow ? BADGE_H + 4 : 0);
            const plotted = series.filter(
              (s) => row.values[s.key] !== undefined,
            );
            const summary = [
              `${row.label}${row.mark ?? ''}:`,
              plotted.map((s) => `${s.label} ${row.text[s.key]}`).join(', '),
              row.badge ? `(${row.badge.text})` : '',
            ].join(' ');
            const badgeWidth = row.badge
              ? textWidth(row.badge.text, 11) + 16
              : 0;
            const tone = row.badge ? badgeTone[row.badge.tone] : undefined;
            return (
              // biome-ignore lint/a11y/noInteractiveElementToNoninteractiveRole: a focusable SVG row that only shows a tooltip; role=img gives it one spoken summary
              <g
                key={row.label}
                className="ecs-chart-row"
                transform={`translate(0, ${layout.top})`}
                opacity={active && active.index !== index ? 0.45 : 1}
                tabIndex={0}
                role="img"
                aria-label={summary}
                onPointerMove={(event) => {
                  const box = containerRef.current?.getBoundingClientRect();
                  if (!box) return;
                  setActive({
                    index,
                    x: event.clientX - box.left,
                    y: event.clientY - box.top,
                  });
                }}
                onPointerLeave={() => setActive(null)}
                onFocus={() => {
                  const scale =
                    (containerRef.current?.clientWidth ?? width) / width;
                  setActive({
                    index,
                    x: width * scale * 0.5,
                    y: (layout.top + layout.height) * scale,
                  });
                }}
                onBlur={() => setActive(null)}
              >
                <rect
                  className="ecs-chart-hit"
                  x={-4}
                  y={-6}
                  width={width + 8}
                  height={layout.height + 12}
                  rx={6}
                  fill="transparent"
                />
                <text
                  x={0}
                  y={14}
                  fontSize={13}
                  fontWeight={600}
                  fill="var(--color-fd-foreground)"
                  fontFamily={
                    row.code
                      ? 'var(--font-mono, ui-monospace, monospace)'
                      : undefined
                  }
                >
                  {row.label}
                  {row.mark && (
                    <tspan fill="var(--color-fd-muted-foreground)">
                      {row.mark}
                    </tspan>
                  )}
                </text>
                {row.badge && tone && (
                  <g
                    transform={
                      layout.badgeBelow
                        ? `translate(0, ${HEADER + 2})`
                        : `translate(${width - badgeWidth}, 0)`
                    }
                  >
                    <rect
                      width={badgeWidth}
                      height={BADGE_H}
                      rx={BADGE_H / 2}
                      fill={tone.fill}
                    />
                    <text
                      x={badgeWidth / 2}
                      y={12.5}
                      fontSize={11}
                      fontWeight={500}
                      textAnchor="middle"
                      fill={tone.text}
                    >
                      {row.badge.text}
                    </text>
                  </g>
                )}
                {plotted.map((s, i) => {
                  const value = row.values[s.key] ?? 0;
                  const barWidth = value > 0 ? Math.max(x(value), 3) : 0;
                  const barY = header + 6 + i * (BAR + BAR_GAP);
                  const delay = `${index * 60 + i * 90}ms`;
                  return (
                    <g key={s.key}>
                      {barWidth > 0 && (
                        <BarRounded
                          className="ecs-chart-bar"
                          style={{ transitionDelay: delay }}
                          x={0}
                          y={barY}
                          width={barWidth}
                          height={BAR}
                          radius={4}
                          right
                          fill={s.color}
                        />
                      )}
                      <text
                        className="ecs-chart-value"
                        style={{
                          transitionDelay: delay,
                          fontVariantNumeric: 'tabular-nums',
                        }}
                        x={barWidth + 6}
                        y={barY + BAR / 2}
                        dy="0.35em"
                        fontSize={11}
                        fill="var(--color-fd-muted-foreground)"
                      >
                        {row.text[s.key]}
                      </text>
                    </g>
                  );
                })}
              </g>
            );
          })}
          <AxisBottom
            top={plotBottom}
            scale={x}
            numTicks={numTicks}
            tickFormat={(value) => tickFormat(Number(value))}
            stroke="var(--color-fd-border)"
            tickStroke="var(--color-fd-border)"
            tickLength={4}
            tickLabelProps={{
              fill: 'var(--color-fd-muted-foreground)',
              fontSize: 11,
              textAnchor: 'middle',
            }}
          />
          <Group top={plotBottom + 38}>
            <text
              x={plotWidth / 2}
              fontSize={11}
              textAnchor="middle"
              fill="var(--color-fd-muted-foreground)"
            >
              {axisLabel}
            </text>
          </Group>
        </svg>
        {activeRow && active && (
          <div
            className="pointer-events-none absolute z-10 min-w-44 rounded-lg border bg-fd-popover px-3 py-2 text-xs text-fd-popover-foreground shadow-lg"
            style={{
              left: Math.min(
                Math.max(active.x + 12, 0),
                (containerRef.current?.clientWidth ?? width) - 200,
              ),
              top: active.y + 14,
            }}
            role="presentation"
          >
            <p
              className={cn(
                'mb-1.5 font-semibold',
                activeRow.code && 'font-mono',
              )}
            >
              {activeRow.label}
            </p>
            {series
              .filter((s) => activeRow.values[s.key] !== undefined)
              .map((s) => (
                <p key={s.key} className="flex items-center gap-2 py-0.5">
                  <span
                    className="inline-block h-0.5 w-3 rounded-full"
                    style={{ background: s.color }}
                  />
                  <strong className="tabular-nums">
                    {activeRow.text[s.key]}
                  </strong>
                  <span className="text-fd-muted-foreground">{s.label}</span>
                </p>
              ))}
            {activeRow.detail && (
              <p className="mt-1 text-fd-muted-foreground">
                {activeRow.detail}
              </p>
            )}
            {activeRow.badge && (
              <p className="mt-1.5 font-medium">{activeRow.badge.text}</p>
            )}
          </div>
        )}
      </div>
      {footnotes && footnotes.length > 0 && (
        <div className="mt-3 space-y-1 text-xs text-fd-muted-foreground">
          {footnotes.map((note, i) => (
            // biome-ignore lint/suspicious/noArrayIndexKey: static footnotes
            <p key={i}>{note}</p>
          ))}
        </div>
      )}
      {table && (
        <details className="mt-4 text-xs">
          <summary className="cursor-pointer text-fd-muted-foreground hover:text-fd-foreground">
            {table.caption}
          </summary>
          <div className="mt-2 overflow-x-auto">
            <table className="w-full border-collapse tabular-nums">
              <thead>
                <tr>
                  {table.head.map((cell, i) => (
                    <th
                      // biome-ignore lint/suspicious/noArrayIndexKey: static columns
                      key={i}
                      className={cn(
                        'border-b px-2 py-1.5 font-medium',
                        i === 0 ? 'text-start' : 'text-end',
                      )}
                    >
                      {cell}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {table.rows.map((cells, r) => (
                  // biome-ignore lint/suspicious/noArrayIndexKey: static rows
                  <tr key={r}>
                    {cells.map((cell, i) => (
                      <td
                        // biome-ignore lint/suspicious/noArrayIndexKey: static columns
                        key={i}
                        className={cn(
                          'border-b px-2 py-1.5',
                          i === 0 ? 'text-start' : 'text-end',
                        )}
                      >
                        {cell}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </figure>
  );
}
