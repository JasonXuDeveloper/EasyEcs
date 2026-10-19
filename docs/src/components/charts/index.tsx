/**
 * <LeadChart />, <PeerChart /> and <BenchChart bench="..." /> for MDX and the
 * home page.
 *
 * The chart code (visx) is a separate chunk loaded only where a chart is used.
 * A prerendered chart hydrates only when it nears the viewport: until then it
 * stays the server's SVG, so a page of many charts does not lay them all out at
 * once on load.
 */
import {
  lazy,
  type ReactNode,
  Suspense,
  use,
  useEffect,
  useId,
  useMemo,
  useState,
  useSyncExternalStore,
} from 'react';
import { cn } from '@/lib/cn';
import { surface } from '@/lib/ui';
import type {
  BenchChartProps,
  LeadChartProps,
  PeerChartProps,
} from './benchmark-charts';

// Prerendering imports the charts up front so the HTML holds the finished SVG
// in place; the browser loads the chunk lazily and keeps the server markup
// until it arrives.
const Charts = import.meta.env.SSR
  ? await import('./benchmark-charts')
  : {
      PeerChart: lazy(() =>
        import('./benchmark-charts').then((m) => ({ default: m.PeerChart })),
      ),
      BenchChart: lazy(() =>
        import('./benchmark-charts').then((m) => ({ default: m.BenchChart })),
      ),
      LeadChart: lazy(() =>
        import('./benchmark-charts').then((m) => ({ default: m.LeadChart })),
      ),
    };

export type {
  BenchChartProps,
  LeadChartProps,
  PeerChartProps,
} from './benchmark-charts';

function Fallback() {
  return (
    <div
      className={cn(
        surface,
        'not-prose my-6 h-80 animate-pulse motion-reduce:animate-none',
      )}
      aria-hidden="true"
    />
  );
}

const subscribe = () => () => {};

/** Opened by Deferred when its chart nears the viewport */
function gate() {
  let open = () => {};
  const promise = new Promise<void>((resolve) => {
    open = resolve;
  });
  return { promise, open };
}

/** Suspends until `wait` resolves; React keeps the server markup meanwhile */
function Wait({
  wait,
  children,
}: {
  wait?: Promise<void>;
  children: ReactNode;
}) {
  if (wait) use(wait);
  return children;
}

/**
 * Hydrates its chart when it comes within a viewport of the screen. Only the
 * hydration of a prerendered page waits (getServerSnapshot is what
 * hydration reads); a chart rendered after a client navigation draws at once.
 * It adds no element (Tabs style a figure that is a panel's only child), so it
 * finds the server's figure by the id it hands the chart (`data-chart`).
 */
function Deferred({ chart }: { chart: (id: string) => ReactNode }) {
  const id = useId();
  const hydrating = useSyncExternalStore(
    subscribe,
    () => false,
    () => true,
  );
  const [wait] = useState(() =>
    hydrating && !import.meta.env.SSR ? gate() : undefined,
  );
  useEffect(() => {
    if (!wait) return;
    const element = document.querySelector(`[data-chart="${CSS.escape(id)}"]`);
    if (!element || !('IntersectionObserver' in window)) {
      wait.open();
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          observer.disconnect();
          wait.open();
        }
      },
      { rootMargin: '100% 0px' },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [wait, id]);
  // The same element after hydration, so the re-render that follows it does
  // not touch the still dehydrated boundary
  return useMemo(
    () => (
      <Suspense fallback={<Fallback />}>
        <Wait wait={wait?.promise}>{chart(id)}</Wait>
      </Suspense>
    ),
    [wait, chart, id],
  );
}

/** The headline tick, the same simulation ported to each library */
export function PeerChart(props: PeerChartProps) {
  return (
    <Deferred chart={(id) => <Charts.PeerChart {...props} chartId={id} />} />
  );
}

/** Any benchmark class as bars, EasyEcs rows in the accent */
export function BenchChart(props: BenchChartProps) {
  return (
    <Deferred chart={(id) => <Charts.BenchChart {...props} chartId={id} />} />
  );
}

/** The head-to-head comparisons, the biggest lead first */
export function LeadChart(props: LeadChartProps) {
  return (
    <Deferred chart={(id) => <Charts.LeadChart {...props} chartId={id} />} />
  );
}
