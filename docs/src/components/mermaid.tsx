/**
 * A ```mermaid fence of the docs, drawn at build time (src/lib/mermaid.ts):
 * the children are the SVG, so the page loads no Mermaid and the diagram is
 * there before hydration. `chart` (the source) is for the Markdown copies.
 * Mermaid's `neutral` theme is grayscale; the dark theme inverts it.
 */
import type { ReactNode } from 'react';

export function Mermaid({ children }: { chart: string; children?: ReactNode }) {
  return (
    <div className="not-prose my-6 flex justify-center overflow-x-auto [&_svg]:h-auto [&_svg]:max-w-full dark:[&_svg]:hue-rotate-180 dark:[&_svg]:invert-[.88]">
      {children}
    </div>
  );
}
