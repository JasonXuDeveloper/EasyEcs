/**
 * Horizontal grouped bar chart drawn with visx (SVG, no canvas).
 *
 * - Prerenders: the SVG is complete in the static HTML at a default width and
 *   re-lays out to the container width after hydration (./index.tsx hydrates
 *   a chart only when it nears the viewport). The plot is memoized: hover,
 *   focus and the reveal flip data attributes and move the tooltip, so they
 *   never re-render the SVG.
 * - Colours are CSS variables (app.css), so light and dark mode need no JS.
 * - Every row is focusable and has a tooltip; a data table sits under the
 *   chart in <details> for screen readers and copy-paste.
 * - Bars grow in when scrolled into view, row after row, the value riding on
 *   the tip of its bar, then the badge pops in (app.css); not with
 *   prefers-reduced-motion.
 * - Labels wrap to the chart's width; `scale="log"` draws values that span
 *   orders of magnitude.
 */
import { AxisBottom } from '@visx/axis';
import { GridColumns } from '@visx/grid';
import { Group } from '@visx/group';
import { scaleLinear, scaleLog } from '@visx/scale';
import { BarRounded } from '@visx/shape';
import {
  type CSSProperties,
  memo,
  type ReactNode,
  type RefObject,
  useEffect,
  useRef,
  useState,
} from 'react';
import { cn } from '@/lib/cn';
import { revealAttribute } from '@/lib/motion';
import { surface } from '@/lib/ui';

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
  /** `data-chart` of the figure (./index.tsx observes it before hydrating) */
  id?: string;
  title: string;
  subtitle?: ReactNode;
  series: ChartSeries[];
  rows: ChartRow[];
  axisLabel: string;
  tickFormat?: (value: number) => string;
  /** Upper end of the axis; defaults to the largest value */
  max?: number;
  /**
   * "log" for values that span orders of magnitude (ratios from 1 to 150):
   * the axis starts at `min` (default 1) and every value is at least that
   */
  scale?: 'linear' | 'log';
  /** Lower end of a log axis */
  min?: number;
  /** The axis ticks, instead of the scale's own */
  tickValues?: number[];
  footnotes?: ReactNode[];
  table?: { caption: string; head: string[]; rows: ReactNode[][] };
}

const BAR = 12;
const BAR_GAP = 4;
const HEADER = 20;
/** Each further line of a label that wraps */
const LINE = 17;
const BADGE_H = 18;
const ROW_GAP = 20;
const AXIS_BAND = 44;
const FALLBACK_WIDTH = 640;
/** Room around the plot for what draws outside it (axis labels, focus rings) */
const PLOT_BLEED = 16;

/** Rough text width; CJK characters (U+2E80-9FFF, U+FF00-FFEF) are about 1em wide. */
function textWidth(text: string, size: number, mono = false) {
  const narrow = size * (mono ? 0.62 : 0.56);
  let width = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    // A surrogate pair is one character
    if (c >= 0xd800 && c <= 0xdbff) i++;
    width +=
      (c >= 0x2e80 && c <= 0x9fff) || (c >= 0xff00 && c <= 0xffef)
        ? size
        : narrow;
  }
  return width;
}

/**
 * A label in lines that fit `max` pixels: words wrap at spaces, CJK text at
 * any character.
 */
function wrapLabel(text: string, max: number, size: number, mono = false) {
  if (textWidth(text, size, mono) <= max) return [text];
  const units = text.match(/[⺀-鿿＀-￯]|[^\s⺀-鿿＀-￯]+\s*|\s+/g) ?? [text];
  const lines: string[] = [];
  let line = '';
  for (const unit of units) {
    if (line && textWidth((line + unit).trimEnd(), size, mono) > max) {
      lines.push(line.trimEnd());
      line = unit.trimStart();
    } else line += unit;
  }
  if (line) lines.push(line.trimEnd());
  return lines;
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
  /** The label's lines, wrapped to the chart's width */
  lines: string[];
  badgeBelow: boolean;
  /** Where the bars start: under the label's lines and the badge */
  header: number;
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

/** Rows start one after another; past the twelfth they start together */
const rowDelay = (index: number) => Math.min(index, 12) * 55;

const defaultTickFormat = (value: number) => String(value);

interface Active {
  index: number;
  x: number;
  y: number;
}

type PlotProps = Pick<
  BarChartProps,
  | 'title'
  | 'series'
  | 'rows'
  | 'axisLabel'
  | 'max'
  | 'scale'
  | 'min'
  | 'tickValues'
> & {
  tickFormat: (value: number) => string;
  width: number;
  containerRef: RefObject<HTMLDivElement | null>;
  setActive: (active: Active | null) => void;
};

/** Marks the row under the pointer or focus; app.css fades the others */
function highlight(row: Element, on: boolean) {
  row.toggleAttribute('data-active', on);
}

/**
 * The SVG, laid out for one width. Memoized: the tooltip state of BarChart
 * does not re-render it, only a new width does.
 */
const Plot = memo(function Plot({
  title,
  series,
  rows,
  axisLabel,
  tickFormat,
  max,
  scale = 'linear',
  min = 1,
  tickValues,
  width,
  containerRef,
  setActive,
}: PlotProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  // Bars grow in when scrolled into view: an attribute, not React state
  useEffect(
    () => (svgRef.current ? revealAttribute(svgRef.current) : undefined),
    [],
  );

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
  const x =
    scale === 'log'
      ? scaleLog<number>({
          domain: [min, Math.max(domainMax, min * 2)],
          range: [0, plotWidth],
          clamp: true,
        })
      : scaleLinear<number>({
          domain: [0, domainMax],
          range: [0, plotWidth],
          nice: max === undefined,
        });
  // A log axis keeps the ticks inside its domain
  const ticks = tickValues?.filter((t) => t <= x.domain()[1]);

  // Vertical layout of each row: label (+ badge), then one bar per series.
  const barCount = (row: ChartRow) =>
    series.filter((s) => row.values[s.key] !== undefined).length;
  const layouts: Layout[] = [];
  let y = 0;
  for (const row of rows) {
    const label = row.label + (row.mark ?? '');
    const lines = wrapLabel(label, width, 13, row.code);
    const labelWidth = textWidth(label, 13, row.code);
    const badgeWidth = row.badge ? textWidth(row.badge.text, 11) + 16 : 0;
    const badgeBelow =
      !!row.badge && (lines.length > 1 || labelWidth + 12 + badgeWidth > width);
    const header =
      HEADER + (lines.length - 1) * LINE + (badgeBelow ? BADGE_H + 4 : 0);
    const n = barCount(row);
    const height = header + 6 + n * BAR + (n - 1) * BAR_GAP;
    layouts.push({ top: y, height, lines, badgeBelow, header });
    y += height + ROW_GAP;
  }
  const plotBottom = y - ROW_GAP + 10;
  const height = plotBottom + AXIS_BAND;
  const numTicks = width < 480 ? 4 : 6;

  return (
    // Off screen the SVG skips style, layout and paint (app.css); its
    // estimated height is the height it draws at: the container's width
    // (cqw, the div around it is the container) times this layout's ratio
    <div
      className="ecs-chart-plot"
      style={{
        containIntrinsicHeight: `auto calc(100cqw * ${(height / width).toFixed(4)} + ${2 * PLOT_BLEED}px)`,
      }}
    >
      {/* biome-ignore lint/a11y/useSemanticElements: <fieldset> cannot live in SVG; the group names the chart */}
      <svg
        ref={svgRef}
        className="ecs-chart block h-auto w-full overflow-visible"
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
          tickValues={ticks}
          stroke="var(--color-fd-border)"
          strokeWidth={1}
        />
        {rows.map((row, index) => {
          const layout = layouts[index];
          const header = layout.header;
          const plotted = series.filter((s) => row.values[s.key] !== undefined);
          const summary = [
            `${row.label}${row.mark ?? ''}:`,
            plotted.map((s) => `${s.label} ${row.text[s.key]}`).join(', '),
            row.badge ? `(${row.badge.text})` : '',
          ].join(' ');
          const badgeWidth = row.badge ? textWidth(row.badge.text, 11) + 16 : 0;
          const tone = row.badge ? badgeTone[row.badge.tone] : undefined;
          return (
            // biome-ignore lint/a11y/noInteractiveElementToNoninteractiveRole: a focusable SVG row that only shows a tooltip; role=img gives it one spoken summary
            <g
              key={row.label}
              className="ecs-chart-row"
              transform={`translate(0, ${layout.top})`}
              tabIndex={0}
              role="img"
              aria-label={summary}
              onPointerMove={(event) => {
                const box = containerRef.current?.getBoundingClientRect();
                if (!box) return;
                highlight(event.currentTarget, true);
                setActive({
                  index,
                  x: event.clientX - box.left,
                  y: event.clientY - box.top,
                });
              }}
              onPointerLeave={(event) => {
                highlight(event.currentTarget, false);
                setActive(null);
              }}
              onFocus={(event) => {
                const scale =
                  (containerRef.current?.clientWidth ?? width) / width;
                highlight(event.currentTarget, true);
                setActive({
                  index,
                  x: width * scale * 0.5,
                  y: (layout.top + layout.height) * scale,
                });
              }}
              onBlur={(event) => {
                highlight(event.currentTarget, false);
                setActive(null);
              }}
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
                fontWeight={500}
                fill="var(--color-fd-foreground)"
                fontFamily={
                  row.code
                    ? 'var(--font-mono, ui-monospace, monospace)'
                    : undefined
                }
              >
                {layout.lines.length === 1 ? (
                  <>
                    {row.label}
                    {row.mark && (
                      <tspan fill="var(--color-fd-muted-foreground)">
                        {row.mark}
                      </tspan>
                    )}
                  </>
                ) : (
                  layout.lines.map((line, i) => (
                    <tspan key={line} x={0} dy={i === 0 ? 0 : LINE}>
                      {line}
                    </tspan>
                  ))
                )}
              </text>
              {row.badge && tone && (
                <g
                  transform={
                    layout.badgeBelow
                      ? `translate(0, ${HEADER + (layout.lines.length - 1) * LINE + 2})`
                      : `translate(${width - badgeWidth}, 0)`
                  }
                >
                  {/* The pop scales this inner group; the outer one keeps its translate */}
                  <g
                    className="ecs-chart-badge"
                    style={{ transitionDelay: `${rowDelay(index) + 420}ms` }}
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
                </g>
              )}
              {plotted.map((s, i) => {
                const value = row.values[s.key] ?? 0;
                const barWidth =
                  value > 0
                    ? Math.max(
                        x(scale === 'log' ? Math.max(value, min) : value) ?? 0,
                        3,
                      )
                    : 0;
                const barY = header + 6 + i * (BAR + BAR_GAP);
                const delay = `${rowDelay(index) + i * 80}ms`;
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
                      style={
                        {
                          transitionDelay: delay,
                          fontVariantNumeric: 'tabular-nums',
                          // Starts at the bar's root and rides out on its tip
                          '--ecs-tip': `${-barWidth}px`,
                        } as CSSProperties
                      }
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
          tickValues={ticks}
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
    </div>
  );
});

export function BarChart({
  id,
  title,
  subtitle,
  series,
  rows,
  axisLabel,
  tickFormat = defaultTickFormat,
  max,
  scale,
  min,
  tickValues,
  footnotes,
  table,
}: BarChartProps) {
  const [containerRef, width] = useWidth<HTMLDivElement>();
  const [active, setActive] = useState<Active | null>(null);
  const activeRow = active ? rows[active.index] : undefined;

  return (
    <figure
      className={cn(surface, 'not-prose my-6 p-4 sm:p-6')}
      data-chart={id}
    >
      <figcaption className="mb-5">
        <p className="text-sm font-medium text-fd-foreground">{title}</p>
        {subtitle && (
          <p className="mt-1 text-xs leading-relaxed text-fd-muted-foreground">
            {subtitle}
          </p>
        )}
      </figcaption>
      {series.length > 1 && (
        <ul className="mb-5 flex flex-wrap gap-x-4 gap-y-1 text-xs text-fd-muted-foreground">
          {series.map((s) => (
            <li key={s.key} className="inline-flex items-center gap-1.5">
              <span
                className="inline-block size-2 rounded-full"
                style={{ background: s.color }}
                aria-hidden="true"
              />
              {s.label}
            </li>
          ))}
        </ul>
      )}
      <div ref={containerRef} className="relative @container">
        <Plot
          title={title}
          series={series}
          rows={rows}
          axisLabel={axisLabel}
          tickFormat={tickFormat}
          max={max}
          scale={scale}
          min={min}
          tickValues={tickValues}
          width={width}
          containerRef={containerRef}
          setActive={setActive}
        />
        {activeRow && active && (
          <div
            className="pointer-events-none absolute z-10 min-w-44 rounded-lg border bg-fd-popover px-3 py-2 text-xs text-fd-popover-foreground shadow-md"
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
                'mb-1.5 font-medium',
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
        <div className="mt-4 space-y-1 border-t pt-3 text-xs text-fd-muted-foreground">
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
