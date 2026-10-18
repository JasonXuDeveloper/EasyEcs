/**
 * Renders a ```mermaid fence of the docs (remarkMdxMermaid turns each one
 * into <Mermaid chart="..." />). Mermaid draws in the browser only: the
 * prerendered HTML holds the diagram source in a <pre> (readable without
 * JavaScript, and by crawlers), replaced by the SVG after hydration. The
 * diagram follows the site theme: the `dark` class Fumadocs puts on <html>.
 */
import { useEffect, useId, useState } from 'react';

function useDark() {
  const [dark, setDark] = useState(false);
  useEffect(() => {
    const root = document.documentElement;
    const update = () => setDark(root.classList.contains('dark'));
    update();
    const observer = new MutationObserver(update);
    observer.observe(root, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);
  return dark;
}

let queue: Promise<unknown> = Promise.resolve();

export function Mermaid({ chart }: { chart: string }) {
  const id = `mermaid-${useId().replace(/[^a-zA-Z0-9]/g, '')}`;
  const dark = useDark();
  const [svg, setSvg] = useState<string>();

  useEffect(() => {
    let cancelled = false;
    // mermaid.render is not reentrant: render one diagram at a time.
    queue = queue.then(async () => {
      const { default: mermaid } = await import('mermaid');
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: 'strict',
        fontFamily: 'inherit',
        theme: dark ? 'dark' : 'neutral',
      });
      try {
        const result = await mermaid.render(`${id}-${dark ? 'd' : 'l'}`, chart);
        if (!cancelled) setSvg(result.svg);
      } catch (error) {
        console.error('[mermaid]', error);
      }
    });
    return () => {
      cancelled = true;
    };
  }, [chart, dark, id]);

  if (!svg)
    return (
      <pre className="not-prose my-6 overflow-x-auto rounded-xl border bg-fd-card p-4 text-xs text-fd-muted-foreground">
        {chart}
      </pre>
    );
  return (
    <div
      className="not-prose my-6 flex justify-center overflow-x-auto [&_svg]:max-w-full [&_svg]:h-auto"
      // biome-ignore lint/security/noDangerouslySetInnerHtml: SVG drawn by mermaid from the docs' own diagram source (securityLevel strict)
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
}
