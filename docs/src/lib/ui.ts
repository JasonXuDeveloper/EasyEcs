/**
 * The site's buttons and surfaces as class names, built on Fumadocs' own
 * `buttonVariants` so links that look like buttons match the docs UI.
 *
 * - button(): pill buttons; `primary` is the accent, solid, and appears once
 *   per view; `secondary` is the outlined neutral one next to it; `ghost` is
 *   for toolbars. Sizes: `md` (40px, page actions) and `sm` (32px, toolbars).
 * - surface: the one card style (hairline border, card fill, no shadow).
 */
import { buttonVariants } from 'fumadocs-ui/components/ui/button';
import { cn } from './cn';

type Variant = 'primary' | 'secondary' | 'ghost';
type Size = 'sm' | 'md';

const sizes: Record<Size, string> = {
  md: 'h-10 gap-2 px-5 text-sm [&_svg]:size-4',
  sm: 'h-8 gap-1.5 px-3 text-[13px] [&_svg]:size-3.5',
};

export function button(
  variant: Variant = 'primary',
  size: Size = 'md',
  className?: string,
) {
  return cn(
    buttonVariants({ variant: variant === 'secondary' ? 'outline' : variant }),
    'rounded-full p-0 whitespace-nowrap',
    sizes[size],
    variant === 'secondary' && 'bg-fd-background',
    className,
  );
}

/** A card: hairline border on the card fill */
export const surface = 'rounded-xl border bg-fd-card text-fd-card-foreground';

/** A card that is a link: the fill darkens on hover, like Fumadocs' Card */
export const surfaceLink = cn(
  surface,
  'transition-colors hover:bg-fd-accent/60',
);
