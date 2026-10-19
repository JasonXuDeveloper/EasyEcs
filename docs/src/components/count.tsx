/**
 * <Count>: a figure that counts up from 0 the first time it comes into view
 * (src/lib/motion.ts). It renders the final text, so the prerendered page,
 * a page without JavaScript and reduced motion all show the real value.
 */
import { type ReactNode, type RefObject, useEffect, useRef } from 'react';
import { cn } from '@/lib/cn';
import { countUp, revealAttribute } from '@/lib/motion';

export function Count({
  children,
  className,
  duration,
  spin,
}: {
  /** The final text: "1.68 ms", "30×" */
  children: ReactNode;
  className?: string;
  /** Length of the count in ms (default 900) */
  duration?: number;
  /** Spin through every value on the way up (stat figures) */
  spin?: boolean;
}) {
  const ref = useRef<HTMLSpanElement>(null);
  useEffect(() => (ref.current ? countUp(ref.current) : undefined), []);
  return (
    <span
      ref={ref}
      className={cn('ecs-count tabular-nums whitespace-nowrap', className)}
      data-count-duration={duration}
      data-count-spin={spin ? '' : undefined}
    >
      {/* The text keeps its place while the digits roll over it (motion.ts) */}
      <span className="ecs-count-text">{children}</span>
    </span>
  );
}

/** Sets `data-hidden` on the element until it is in view: app.css grows its bars in */
export function useRevealAttribute<T extends Element>(): RefObject<T | null> {
  const ref = useRef<T>(null);
  useEffect(() => (ref.current ? revealAttribute(ref.current) : undefined), []);
  return ref;
}
