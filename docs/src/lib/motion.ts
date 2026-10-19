/**
 * The motion of the number moments: bars that grow in and figures that count
 * up from 0 when they first come into view.
 *
 * - The prerendered HTML holds the final state (crawlers, the Markdown copies,
 *   no JavaScript, print); motion starts only after hydration and only for
 *   what is not on screen yet, or is on screen when the page hydrates.
 * - Nothing animates with prefers-reduced-motion.
 * - It is transforms and opacity only: CSS transitions for the bars (app.css),
 *   the Web Animations of `number-flow` for the digits (loaded on the first
 *   count, a few KB), never a React render per frame.
 */

function reducedMotion() {
  return (
    typeof window === 'undefined' ||
    window.matchMedia('(prefers-reduced-motion: reduce)').matches
  );
}

/**
 * Hides `element` (`show(false)`) when it is below the fold and shows it again
 * once scrolled into view; an element already on screen replays its animation
 * once. Does nothing with reduced motion. Returns the cleanup.
 */
export function reveal(element: Element, show: (shown: boolean) => void) {
  if (reducedMotion() || !('IntersectionObserver' in window)) return;
  // The observer says where the element is, so hydration reads no layout
  let first = true;
  let frame = 0;
  const observer = new IntersectionObserver(
    (entries) => {
      const visible = entries.some((entry) => entry.isIntersecting);
      if (first) {
        first = false;
        show(false);
        if (!visible) return;
        observer.disconnect();
        frame = requestAnimationFrame(() => {
          frame = requestAnimationFrame(() => show(true));
        });
      } else if (visible) {
        observer.disconnect();
        show(true);
      }
    },
    // Once its top is a little way into the screen: a tall chart starts
    // when its first rows show, not when a quarter of it does
    { rootMargin: '0px 0px -10% 0px' },
  );
  observer.observe(element);
  return () => {
    observer.disconnect();
    cancelAnimationFrame(frame);
  };
}

/** Hides `element` with `data-hidden` until it is in view (app.css draws the growth) */
export function revealAttribute(element: Element) {
  return reveal(element, (shown) =>
    element.toggleAttribute('data-hidden', !shown),
  );
}

/* ------------------------------------------------------------------ */
/* Count-up                                                            */
/* ------------------------------------------------------------------ */

/** "1.68 ms", "30×", "1,024 B", "≈2.5×": what is before, in and after the number */
const NUMBER = /^([^\d]*)(\d[\d,]*(?:\.\d+)?)(.*)$/s;

interface Count {
  prefix: string;
  value: number;
  decimals: number;
  grouping: boolean;
  suffix: string;
}

/** The number in a figure's text, or undefined when there is none to count */
export function parseCount(text: string): Count | undefined {
  const match = NUMBER.exec(text);
  if (!match) return undefined;
  const digits = match[2];
  const value = Number.parseFloat(digits.replaceAll(',', ''));
  if (!Number.isFinite(value) || value === 0) return undefined;
  return {
    prefix: match[1],
    value,
    decimals: digits.split('.')[1]?.length ?? 0,
    grouping: digits.includes(','),
    suffix: match[3],
  };
}

/** The strong ease-out of every count and bar (app.css: --ecs-ease-out) */
const EASE = 'cubic-bezier(0.16, 1, 0.3, 1)';
/** Figures that come into view together start this far apart */
const STAGGER = 110;

/** number-flow defines <number-flow>; its plugins hold the continuous spin */
let library: Promise<typeof import('number-flow/plugins')> | undefined;
const load = () =>
  Promise.all([import('number-flow'), import('number-flow/plugins')]).then(
    ([, plugins]) => plugins,
  );
let observer: IntersectionObserver | undefined;
const pending = new Set<HTMLElement>();

function observe() {
  observer ??= new IntersectionObserver(
    (entries) => {
      const start: IntersectionObserverEntry[] = [];
      for (const entry of entries) {
        const element = entry.target as HTMLElement;
        if (entry.isIntersecting) {
          observer?.unobserve(element);
          pending.delete(element);
          // A number in running text already on screen when the page
          // hydrates stays as it is: a reader may be on that line
          if (element.dataset.count || element.dataset.countSpin !== undefined)
            start.push(entry);
        } else if (!element.dataset.count) {
          // Below the fold: hide the digits (their width stays) until in view
          element.dataset.count = 'armed';
        }
      }
      if (start.length) play(start);
    },
    // Counts once it is a little way into the screen, not on its edge
    { rootMargin: '0px 0px -8% 0px' },
  );
  return observer;
}

function settle(element: HTMLElement) {
  delete element.dataset.count;
}

/**
 * Counts the figures that came into view together, one after the other. The
 * digits roll in an overlay over the figure's own text, which keeps its
 * place in the line (only made transparent): nothing in the page's layout
 * changes, so counting never lays out the page again.
 */
async function play(entries: IntersectionObserverEntry[]) {
  library ??= load();
  let flow: typeof import('number-flow/plugins');
  try {
    flow = await library;
  } catch {
    // Offline or blocked: the final values, at once
    for (const entry of entries) settle(entry.target as HTMLElement);
    return;
  }
  let order = 0;
  for (const { target } of entries) {
    const element = target as HTMLElement;
    const count = parseCount(element.textContent ?? '');
    if (!count || !element.isConnected) {
      settle(element);
      continue;
    }
    const duration = Number(element.dataset.countDuration ?? 900);
    const delay = order++ * STAGGER;
    const digits = document.createElement('number-flow');
    digits.format = {
      minimumFractionDigits: count.decimals,
      maximumFractionDigits: count.decimals,
      useGrouping: count.grouping,
    };
    digits.locales = 'en-US';
    digits.numberPrefix = count.prefix;
    digits.numberSuffix = count.suffix;
    // A stat figure spins through every value on its way up; a number in a
    // sentence or a table only rolls its digits, briefly, to keep the line calm
    if (element.dataset.countSpin !== undefined)
      digits.plugins = [flow.continuous];
    digits.transformTiming = { duration, easing: EASE };
    digits.opacityTiming = { duration: 300, easing: 'ease-out' };
    // Screen readers read the figure's own text, which stays in place
    digits.setAttribute('aria-hidden', 'true');
    digits.update(0);
    const roll = document.createElement('span');
    roll.className = 'ecs-count-roll';
    roll.appendChild(digits);
    element.appendChild(roll);
    element.dataset.count = 'playing';
    let finished = false;
    const done = () => {
      if (finished) return;
      finished = true;
      roll.remove();
      settle(element);
    };
    digits.addEventListener('animationsfinish', done, { once: true });
    // Each count starts in a task of its own (measuring its digits is a
    // layout), after the frame that draws the zeros
    window.setTimeout(() => {
      if (!digits.isConnected) return;
      digits.update(count.value);
      // A tab in the background runs no animations: never leave it half-way
      window.setTimeout(done, duration + 400);
    }, delay + 50);
  }
}

/**
 * Counts `element`'s number up from 0 the first time it comes into view
 * (`data-count-duration` sets the length in ms). Does nothing with reduced
 * motion. Returns the cleanup.
 */
export function countUp(element: HTMLElement) {
  if (reducedMotion() || !('IntersectionObserver' in window)) return;
  if (!parseCount(element.textContent ?? '')) return;
  pending.add(element);
  observe().observe(element);
  return () => {
    observer?.unobserve(element);
    pending.delete(element);
    if (element.dataset.count === 'armed') settle(element);
  };
}

// Printing shows every figure, scrolled past or not
if (typeof window !== 'undefined')
  window.addEventListener('beforeprint', () => {
    for (const element of pending) settle(element);
  });
