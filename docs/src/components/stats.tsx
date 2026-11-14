/**
 * The headline numbers of a page as stat blocks, for MDX (src/mdx-components.tsx)
 * and the Markdown copies (src/lib/markdown.tsx, `statMarkdown`):
 *
 * <Stats>
 *   <Stat hero k="tick.oneWorld" label="One server tick">100,000 entities, one world</Stat>
 *   <Stat lead library="Arch" />
 *   <Stat count="ahead" of="total" library="Arch" label="Faster in" />
 * </Stats>
 *
 * A stat shows one figure: a named number (`k`, keys in src/lib/benchmarks.ts),
 * a lead of the head-to-head comparisons (`lead`, the biggest first; `lead="1"`
 * the next one), or a count of them (`count`, optionally `of` another count).
 * The label is the short line above it, the children the line under it; a
 * lead without them names what is measured and both times. `hero` spans the
 * row with the huge figure of the home page. Every number comes from
 * src/data/bench.json; the figures count up when they come into view.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import type { ReactNode } from 'react';
import { type Bar, Bars } from '@/components/bars';
import { Count } from '@/components/count';
import {
  benchValue,
  formatRatio,
  formatTime,
  libraryName,
} from '@/lib/benchmarks';
import { cn } from '@/lib/cn';
import {
  type FeatureResult,
  featureComparison,
  featureCounts,
} from '@/lib/feature-comparison';
import type { Locale } from '@/lib/i18n';

type CountKey = keyof ReturnType<typeof featureCounts>;

export interface StatProps {
  /** A named number of src/lib/benchmarks.ts */
  k?: string;
  /** A lead of the comparisons, the biggest first: `lead` or `lead="0"` is the biggest */
  lead?: boolean | number | string;
  /** How many comparisons (FeatureCount's keys) */
  count?: CountKey;
  /** Shown after the count: "38 / 46" */
  of?: CountKey;
  /** Leads and counts against this library instead of the fastest other one */
  library?: string;
  label?: string;
  hero?: boolean;
  children?: ReactNode;
  lang?: Locale;
}

interface StatData {
  label: string;
  /** The figure: the number that counts, then a unit set smaller ("ms", "/ 46") */
  value: string;
  unit?: string;
  note?: ReactNode;
  lead?: FeatureResult;
}

/** "1.68 ms" -> "1.68" and "ms"; "30×" stays whole (the sign belongs to the figure) */
function split(text: string) {
  const space = text.lastIndexOf(' ');
  return space > 0 && /\d$/.test(text.slice(0, space))
    ? { value: text.slice(0, space), unit: text.slice(space + 1) }
    : { value: text };
}

/** What a stat shows; shared by the page and its Markdown copy */
export function statData(props: StatProps, lang: Locale): StatData {
  const zh = lang === 'zh';
  if (props.lead !== undefined && props.lead !== false) {
    const rank = props.lead === true ? 0 : Number(props.lead);
    const item = featureComparison('lead', props.library)[rank];
    if (!item) throw new Error(`No lead ${rank} in the comparisons`);
    const other = libraryName(item.versus.library);
    return {
      label:
        props.label ??
        (zh
          ? `${item.label.zh}，对比 ${other}`
          : `${item.label.en}, vs ${other}`),
      value: formatRatio(item.ratio),
      unit: zh ? '快' : 'faster',
      note:
        props.children ??
        (zh
          ? `EasyEcs ${formatTime(item.ours.meanNs)}，${other} ${formatTime(item.versus.row.meanNs)}`
          : `EasyEcs ${formatTime(item.ours.meanNs)}, ${other} ${formatTime(item.versus.row.meanNs)}`),
      lead: item,
    };
  }
  if (props.count) {
    const counts = featureCounts(props.library);
    return {
      label: props.label ?? '',
      value: String(counts[props.count]),
      unit: props.of ? `/ ${counts[props.of]}` : undefined,
      note: props.children,
    };
  }
  if (!props.k) throw new Error('<Stat> needs k, lead or count');
  return {
    label: props.label ?? '',
    ...split(benchValue(props.k)),
    note: props.children,
  };
}

/** The two bars of a lead: EasyEcs against the library it is measured against */
const leadBars = (l: FeatureResult): Bar[] => [
  { name: 'EasyEcs', ns: l.ours.meanNs, ours: true },
  { name: libraryName(l.versus.library), ns: l.versus.row.meanNs },
];

/** A figure: the number counts up, the unit stays */
export function Figure({
  value,
  unit,
  hero,
  className,
}: {
  value: string;
  unit?: string;
  hero?: boolean;
  className?: string;
}) {
  return (
    <span
      className={cn(
        'ecs-stat-figure flex flex-wrap items-baseline gap-x-2 gap-y-1 font-extrabold tracking-tighter',
        hero ? 'text-7xl/none @xl:text-8xl/none' : 'text-5xl/none',
        className,
      )}
    >
      <Count
        className="ecs-gradient-text -mb-2 pb-2"
        duration={hero ? 1600 : 1200}
        spin
      >
        {value}
      </Count>
      {unit && (
        <span
          className={cn(
            'font-semibold tracking-tight text-fd-muted-foreground',
            hero ? 'text-2xl sm:text-3xl' : 'text-xl',
          )}
        >
          {unit}
        </span>
      )}
    </span>
  );
}

export function Stat(props: StatProps) {
  const { locale } = useI18n();
  const lang: Locale = props.lang ?? (locale === 'zh' ? 'zh' : 'en');
  const data = statData(props, lang);
  return (
    <div
      className={cn(
        'ecs-stat flex min-w-0 flex-col gap-3 rounded-xl border bg-fd-card p-5 text-fd-card-foreground',
        props.hero &&
          'ecs-stat-hero col-span-full @xl:grid @xl:grid-cols-[auto_minmax(0,1fr)] @xl:items-end @xl:gap-x-10 @xl:p-7',
      )}
    >
      <div className="flex min-w-0 flex-col gap-3">
        {data.label && (
          <p className="text-sm text-fd-muted-foreground">{data.label}</p>
        )}
        <Figure value={data.value} unit={data.unit} hero={props.hero} />
      </div>
      {(data.note || (props.hero && data.lead)) && (
        <div className="flex min-w-0 flex-col gap-3 text-sm leading-snug text-pretty text-fd-muted-foreground">
          {data.note && <div>{data.note}</div>}
          {props.hero && data.lead && <Bars rows={leadBars(data.lead)} />}
        </div>
      )}
    </div>
  );
}

/** A row of stats: two or three across, a hero stat across the whole row */
export function Stats({ children }: { children: ReactNode }) {
  return (
    // Columns by the width of the content column, not of the window
    <div className="not-prose my-8 @container">
      <div className="grid grid-cols-[repeat(auto-fit,minmax(min(100%,10.5rem),1fr))] gap-3">
        {children}
      </div>
    </div>
  );
}
