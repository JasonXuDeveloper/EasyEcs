/**
 * <EcsDemo />: the live demo of content/demo.mdx. The world of
 * demo/EasyEcs.Demo runs in the browser on .NET's WebAssembly runtime, loaded
 * only when this component mounts (src/components/demo/runtime.ts). Each
 * animation frame calls Step(dt) once and draws the sparks from the shared
 * frame array into one ImageData: no per-entity interop, no allocation per
 * frame on either side. Colors come from the theme tokens and follow dark mode.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { Pause, Play, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { cn } from '@/lib/cn';
import { type DemoRuntime, loadDemoRuntime } from './runtime';

const counts = [1_000, 10_000, 50_000, 100_000] as const;
/** Glow and opacity levels of the color table */
const levels = 16;
/** A frame longer than this (a background tab, a breakpoint) is cut short */
const maxDt = 1 / 20;

type Status = 'loading' | 'running' | 'error';

interface Stats {
  entities: number;
  updateMs: number;
  fps: number;
}

const text = {
  en: {
    entities: 'Entities',
    update: 'ms per world.Update()',
    fps: 'FPS',
    pause: 'Pause',
    resume: 'Resume',
    reset: 'Reset',
    count: 'Entity count',
    loading: 'Loading the .NET runtime…',
    error: 'The demo could not start in this browser.',
    noscript: 'The demo needs JavaScript and WebAssembly.',
    canvas: 'Sparks moved by the EasyEcs systems below',
  },
  zh: {
    entities: '实体',
    update: '每次 world.Update() 耗时（ms）',
    fps: 'FPS',
    pause: '暂停',
    resume: '继续',
    reset: '重置',
    count: '实体数量',
    loading: '正在加载 .NET 运行时…',
    error: '这个浏览器无法运行演示。',
    noscript: '演示需要 JavaScript 和 WebAssembly。',
    canvas: '由下方 EasyEcs 系统驱动的粒子',
  },
};

/** A CSS color (any syntax the browser knows) as [r, g, b] */
function rgbOf(color: string, probe: CanvasRenderingContext2D): number[] {
  probe.clearRect(0, 0, 1, 1);
  probe.fillStyle = color;
  probe.fillRect(0, 0, 1, 1);
  const [r, g, b] = probe.getImageData(0, 0, 1, 1).data;
  return [r, g, b];
}

/**
 * The pixel values of the canvas as little-endian RGBA words: the background,
 * then every (glow, opacity) level mixed from the theme's muted text color
 * towards its primary color, over the background.
 */
function palette(element: HTMLElement): {
  background: number;
  table: Uint32Array;
} {
  const style = getComputedStyle(element);
  const probe = document.createElement('canvas').getContext('2d', {
    willReadFrequently: true,
  });
  const token = (name: string, fallback: string) =>
    style.getPropertyValue(name).trim() || fallback;
  if (!probe)
    return { background: 0xffffffff, table: new Uint32Array(levels * levels) };
  const bg = rgbOf(token('--color-fd-card', '#fff'), probe);
  const base = rgbOf(token('--color-fd-muted-foreground', '#666'), probe);
  const glow = rgbOf(token('--color-fd-primary', '#36c'), probe);
  const word = (c: number[]) =>
    ((255 << 24) | (c[2] << 16) | (c[1] << 8) | c[0]) >>> 0;
  const table = new Uint32Array(levels * levels);
  for (let g = 0; g < levels; g++) {
    const tint = g / (levels - 1);
    const color = base.map((v, i) => v + (glow[i] - v) * tint);
    for (let a = 0; a < levels; a++) {
      const alpha = (a + 1) / levels;
      table[g * levels + a] = word(
        color.map((v, i) => Math.round(bg[i] + (v - bg[i]) * alpha)),
      );
    }
  }
  return { background: word(bg), table };
}

export function EcsDemo() {
  const t = text[useI18n().locale === 'zh' ? 'zh' : 'en'];
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const pausedRef = useRef(false);
  const [count, setCount] = useState<number>(10_000);
  const [generation, setGeneration] = useState(0);
  const [paused, setPaused] = useState(false);
  const [status, setStatus] = useState<Status>('loading');
  const [stats, setStats] = useState<Stats>({
    entities: 0,
    updateMs: 0,
    fps: 0,
  });

  pausedRef.current = paused;

  useEffect(() => {
    const wrap = wrapRef.current;
    const canvas = canvasRef.current;
    const context = canvas?.getContext('2d');
    if (!wrap || !canvas || !context) return;

    // `generation` restarts this effect: the Reset button.
    void generation;
    let runtime: DemoRuntime | undefined;
    let disposed = false;
    let frameId = 0;
    let visible = true;
    let width = 0;
    let height = 0;
    let scale = 1;
    let image: ImageData | undefined;
    let pixels = new Uint32Array(0);
    let colors = palette(wrap);
    let frameStart = 0;
    let stride = 4;
    let sparks = 0;
    let dirty = true;
    let last = 0;
    let smoothMs = -1;
    let frames = 0;
    let fpsSince = 0;
    let fps = 0;
    let steps = 0;

    function resize() {
      if (!wrap || !canvas) return;
      width = Math.max(1, wrap.clientWidth);
      height = Math.max(1, wrap.clientHeight);
      scale = window.devicePixelRatio || 1;
      canvas.width = Math.round(width * scale);
      canvas.height = Math.round(height * scale);
      image = context?.createImageData(canvas.width, canvas.height);
      pixels = image ? new Uint32Array(image.data.buffer) : new Uint32Array(0);
      runtime?.exports.Resize(width, height);
      dirty = true;
    }

    function draw() {
      if (!image || !runtime || !canvas || !context) return;
      pixels.fill(colors.background);
      const heap = runtime.heap();
      const w = canvas.width;
      const h = canvas.height;
      const size = Math.max(1, Math.round(1.5 * scale));
      const glowSize = size + Math.max(1, Math.round(scale));
      const table = colors.table;
      for (let i = 0, p = frameStart; i < sparks; i++, p += stride) {
        const alpha = heap[p + 3];
        if (alpha <= 0) continue;
        const x0 = (heap[p] * scale) | 0;
        const y0 = (heap[p + 1] * scale) | 0;
        const g = Math.min(levels - 1, (heap[p + 2] * levels) | 0);
        const a = Math.min(levels - 1, (alpha * levels) | 0);
        const color = table[g * levels + a];
        // A glowing spark is drawn larger, so bounces stand out
        const side = g > 0 ? glowSize : size;
        const x1 = Math.min(w, x0 + side);
        const y1 = Math.min(h, y0 + side);
        for (let y = Math.max(0, y0); y < y1; y++) {
          const row = y * w;
          for (let x = Math.max(0, x0); x < x1; x++) pixels[row + x] = color;
        }
      }
      context.putImageData(image, 0, 0);
      dirty = false;
    }

    function frame(now: number) {
      frameId = requestAnimationFrame(frame);
      if (!runtime) return;
      const dt = last ? Math.min(maxDt, (now - last) / 1000) : 1 / 60;
      last = now;
      if (visible && !pausedRef.current) {
        sparks = runtime.exports.Step(dt);
        // The first update fills the world: not a steady-state tick
        if (steps++ > 0) {
          const ms = runtime.exports.UpdateMs();
          smoothMs = smoothMs < 0 ? ms : smoothMs + (ms - smoothMs) * 0.05;
        }
        dirty = true;
      }
      if (dirty && visible) draw();
      frames++;
      if (now - fpsSince >= 500) {
        fps = fpsSince ? (frames * 1000) / (now - fpsSince) : 0;
        frames = 0;
        fpsSince = now;
        setStats({
          entities: runtime.exports.EntityCount(),
          updateMs: Math.max(0, smoothMs),
          fps: pausedRef.current ? 0 : fps,
        });
      }
    }

    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(wrap);
    const visibility = new IntersectionObserver((entries) => {
      visible = entries.some((entry) => entry.isIntersecting);
      last = 0;
    });
    visibility.observe(wrap);
    // Fumadocs switches the theme with the `dark` class on <html>
    const theme = new MutationObserver(() => {
      colors = palette(wrap);
      dirty = true;
    });
    theme.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['class'],
    });

    setStatus((s) => (s === 'running' ? s : 'loading'));
    resize();
    loadDemoRuntime()
      .then((loaded) => {
        if (disposed) return;
        runtime = loaded;
        loaded.exports.Reset(count, width, height);
        // Reset may replace the frame array: its address is read after it
        frameStart = loaded.exports.FrameAddress() >> 2;
        stride = loaded.exports.FrameStride();
        setStatus('running');
        frameId = requestAnimationFrame(frame);
      })
      .catch((error: unknown) => {
        console.error('[demo]', error);
        if (!disposed) setStatus('error');
      });

    return () => {
      disposed = true;
      cancelAnimationFrame(frameId);
      resizeObserver.disconnect();
      visibility.disconnect();
      theme.disconnect();
    };
  }, [count, generation]);

  const button =
    'inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-sm font-medium transition-colors hover:bg-fd-accent hover:text-fd-accent-foreground disabled:opacity-50';

  return (
    <div className="not-prose my-6 overflow-hidden rounded-xl border bg-fd-card">
      <div className="flex flex-wrap items-center gap-2 border-b p-3">
        <fieldset className="inline-flex overflow-hidden rounded-md border">
          <legend className="sr-only">{t.count}</legend>
          {counts.map((n) => (
            <button
              key={n}
              type="button"
              aria-pressed={count === n}
              onClick={() => setCount(n)}
              className={cn(
                'px-2.5 py-1 text-sm tabular-nums transition-colors',
                count === n
                  ? 'bg-fd-primary text-fd-primary-foreground'
                  : 'hover:bg-fd-accent hover:text-fd-accent-foreground',
              )}
            >
              {n >= 1000 ? `${n / 1000}k` : n}
            </button>
          ))}
        </fieldset>
        <button
          type="button"
          className={button}
          disabled={status !== 'running'}
          onClick={() => setPaused((p) => !p)}
        >
          {paused ? (
            <Play className="size-3.5" />
          ) : (
            <Pause className="size-3.5" />
          )}
          {paused ? t.resume : t.pause}
        </button>
        <button
          type="button"
          className={button}
          disabled={status !== 'running'}
          onClick={() => setGeneration((g) => g + 1)}
        >
          <RotateCcw className="size-3.5" />
          {t.reset}
        </button>
      </div>
      <div
        ref={wrapRef}
        className="relative h-[clamp(260px,55vw,440px)] w-full"
      >
        <canvas
          ref={canvasRef}
          className="absolute inset-0 size-full"
          role="img"
          aria-label={t.canvas}
        />
        {status !== 'running' && (
          <div className="absolute inset-0 flex items-center justify-center p-4 text-center text-sm text-fd-muted-foreground">
            {status === 'error' ? t.error : t.loading}
            <noscript>{t.noscript}</noscript>
          </div>
        )}
      </div>
      <dl className="grid grid-cols-3 divide-x border-t text-center">
        {[
          [t.entities, stats.entities.toLocaleString('en-US')],
          [t.update, stats.updateMs.toFixed(2)],
          [t.fps, stats.fps.toFixed(0)],
        ].map(([label, value]) => (
          <div key={label} className="px-2 py-2">
            <dd className="font-mono text-lg font-semibold tabular-nums">
              {value}
            </dd>
            <dt className="text-xs text-fd-muted-foreground">{label}</dt>
          </div>
        ))}
      </dl>
    </div>
  );
}
