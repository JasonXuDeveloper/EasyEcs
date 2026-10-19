/**
 * Horizontal bars on a linear scale, the longest time full width: EasyEcs in
 * the accent, the others in grey. The name and the time are text, so a
 * screen reader reads "EasyEcs 146 μs, DefaultEcs 4.33 ms"; the bars are
 * decoration. They grow in, one after the other, the first time the list
 * comes into view (app.css, `.ecs-bars-bar`).
 */
import { useRevealAttribute } from '@/components/count';
import { formatTime } from '@/lib/benchmarks';
import { cn } from '@/lib/cn';

export interface Bar {
  name: string;
  ns: number;
  ours?: boolean;
}

export function Bars({ rows }: { rows: Bar[] }) {
  const ref = useRevealAttribute<HTMLUListElement>();
  const max = Math.max(...rows.map((r) => r.ns));
  return (
    <ul ref={ref} className="flex flex-col gap-1.5">
      {rows.map((r, i) => (
        <li
          key={r.name}
          className="grid grid-cols-[5.5rem_minmax(0,1fr)_4.5rem] items-center gap-3 text-xs"
        >
          <span
            className={cn(
              'truncate',
              r.ours ? 'font-medium' : 'text-fd-muted-foreground',
            )}
          >
            {r.name}
          </span>
          <span className="relative h-1.5" aria-hidden="true">
            <span
              className="ecs-bars-bar absolute inset-y-0 left-0 min-w-1 rounded-full"
              style={{
                width: `${(r.ns / max) * 100}%`,
                background: r.ours
                  ? 'var(--ecs-chart-ours)'
                  : 'var(--ecs-chart-other)',
                transitionDelay: `${i * 140}ms`,
              }}
            />
          </span>
          <span
            className={cn(
              'text-right tabular-nums',
              r.ours ? 'font-medium' : 'text-fd-muted-foreground',
            )}
          >
            {formatTime(r.ns)}
          </span>
        </li>
      ))}
    </ul>
  );
}
