/**
 * <PeerChart /> and <BenchChart bench="..." /> for MDX and the home page.
 *
 * The chart code (visx) is a separate chunk loaded only where a chart is used.
 */
import { lazy, Suspense } from 'react';
import { cn } from '@/lib/cn';
import { surface } from '@/lib/ui';
import type { BenchChartProps, PeerChartProps } from './benchmark-charts';

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
    };

export type { BenchChartProps, PeerChartProps } from './benchmark-charts';

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

/** The headline tick, the same simulation ported to each library */
export function PeerChart(props: PeerChartProps) {
  return (
    <Suspense fallback={<Fallback />}>
      <Charts.PeerChart {...props} />
    </Suspense>
  );
}

/** Any benchmark class as bars, EasyEcs rows in the accent */
export function BenchChart(props: BenchChartProps) {
  return (
    <Suspense fallback={<Fallback />}>
      <Charts.BenchChart {...props} />
    </Suspense>
  );
}
