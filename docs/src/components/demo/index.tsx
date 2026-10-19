/**
 * <EcsDemo />: the live demo of content/demo.mdx. The world of
 * demo/EasyEcs.Demo runs in the browser on .NET's WebAssembly runtime, loaded
 * only when this component mounts (src/components/demo/runtime.ts). Each
 * animation frame calls Step(dt) once and hands the shared frame array to the
 * WebGL renderer (renderer.ts) in one upload: no per-entity interop, no
 * allocation per frame on either side. The pointer over the canvas is passed
 * to the world as data; colors come from the theme tokens and follow dark mode.
 */
import { useI18n } from 'fumadocs-ui/contexts/i18n';
import { Pause, Play, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { cn } from '@/lib/cn';
import { button, surface } from '@/lib/ui';
import { createRenderer, type Palette, type Renderer } from './renderer';
import { type DemoRuntime, loadDemoRuntime } from './runtime';

const counts = [1_000, 10_000, 50_000, 100_000] as const;
/** A frame longer than this (a background tab, a breakpoint) is cut short */
const maxDt = 1 / 20;
/** Device pixels per CSS pixel, at most: soft sparks gain nothing past it */
const maxScale = 2;

type Status = 'loading' | 'running' | 'error';

interface Stats {
  entities: number;
  updateMs: number;
  fps: number;
  /** Motion group times: batch methods with SIMD, and per-entity methods */
  batchMs: number;
  entityMs: number;
}

const text = {
  en: {
    entities: 'Entities',
    update: 'ms per update, all systems',
    fps: 'FPS',
    pause: 'Pause',
    resume: 'Resume',
    reset: 'Reset',
    count: 'Entity count',
    motion: 'The same motion written two ways, measured live:',
    batched: '[ForEach] over Span + SIMD',
    perEntity: '[ForEach] with ref / in, one entity at a time',
    faster: (x: string) => `the Span version is ${x} faster`,
    loading: 'Loading the .NET runtime…',
    error: 'The demo could not start in this browser.',
    noscript: 'The demo needs JavaScript and WebAssembly.',
    canvas: 'Sparks moved by the EasyEcs systems below',
    hint: 'Move the pointer over the sparks to pull them',
    webgl: 'The demo needs WebGL 2.',
  },
  zh: {
    entities: '实体',
    update: '每次更新耗时（ms，全部系统）',
    fps: 'FPS',
    pause: '暂停',
    resume: '继续',
    reset: '重置',
    count: '实体数量',
    motion: '同一段运动逻辑的两种写法，实时测量：',
    batched: 'Span 版 [ForEach] + SIMD',
    perEntity: 'ref / in 版 [ForEach]（逐个实体）',
    faster: (x: string) => `Span 版快 ${x}`,
    loading: '正在加载 .NET 运行时…',
    error: '这个浏览器无法运行演示。',
    noscript: '演示需要 JavaScript 和 WebAssembly。',
    canvas: '由下方 EasyEcs 系统驱动的粒子',
    hint: '把指针移到粒子上，就能把它们吸过来',
    webgl: '演示需要 WebGL 2。',
  },
};

type Rgb = [number, number, number];

/** A CSS color (any syntax the browser knows) as sRGB 0..1 */
function rgbOf(color: string, probe: CanvasRenderingContext2D): Rgb {
  probe.clearRect(0, 0, 1, 1);
  probe.fillStyle = color;
  probe.fillRect(0, 0, 1, 1);
  const [r, g, b] = probe.getImageData(0, 0, 1, 1).data;
  return [r / 255, g / 255, b / 255];
}

const linear = (c: Rgb): Rgb =>
  c.map((v) =>
    v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4,
  ) as Rgb;
const mix = (a: Rgb, b: Rgb, t: number): Rgb =>
  a.map((v, i) => v + (b[i] - v) * t) as Rgb;

/** `c` with its hue turned by `degrees` (a rotation about the grey axis) */
function turn(c: Rgb, degrees: number): Rgb {
  const a = (degrees * Math.PI) / 180;
  const cos = Math.cos(a);
  const sin = Math.sin(a);
  const k = (1 - cos) / 3;
  const s = Math.sqrt(1 / 3) * sin;
  const m = [cos + k, k - s, k + s];
  const [r, g, b] = c;
  return [
    r * m[0] + g * m[1] + b * m[2],
    r * m[2] + g * m[0] + b * m[1],
    r * m[1] + g * m[2] + b * m[0],
  ].map((v) => Math.min(1, Math.max(0, v))) as Rgb;
}

/**
 * The colors of the canvas, from the theme: the card behind it and three
 * stops around the accent. Slow sparks lean violet, fast ones cyan; on dark
 * the hottest glow towards white, on light they deepen like ink.
 */
function palette(element: HTMLElement): Palette {
  const style = getComputedStyle(element);
  const token = (name: string, fallback: string) =>
    style.getPropertyValue(name).trim() || fallback;
  const dark = document.documentElement.classList.contains('dark');
  const probe = document
    .createElement('canvas')
    .getContext('2d', { willReadFrequently: true });
  const background: Rgb = probe
    ? rgbOf(token('--color-fd-card', dark ? '#0a0a0a' : '#fff'), probe)
    : dark
      ? [0.04, 0.04, 0.04]
      : [1, 1, 1];
  const accent: Rgb = probe
    ? rgbOf(token('--color-fd-primary', '#36c'), probe)
    : [0.2, 0.4, 0.8];
  const cold = turn(accent, 38);
  const cyan = turn(accent, -32);
  return dark
    ? {
        background: linear(background),
        cold: linear(mix(cold, [0, 0, 0], 0.45)),
        warm: linear(accent),
        hot: linear(mix(cyan, [1, 1, 1], 0.55)),
        dark,
      }
    : {
        background: linear(background),
        cold: linear(mix(cold, [1, 1, 1], 0.15)),
        warm: linear(accent),
        hot: linear(mix(turn(accent, -12), [0, 0, 0], 0.3)),
        dark,
      };
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
  const [noGl, setNoGl] = useState(false);
  const [stats, setStats] = useState<Stats>({
    entities: 0,
    updateMs: 0,
    fps: 0,
    batchMs: 0,
    entityMs: 0,
  });

  pausedRef.current = paused;

  // A visitor who asked for less motion starts paused and presses Resume to watch
  useEffect(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches)
      setPaused(true);
  }, []);

  useEffect(() => {
    const wrap = wrapRef.current;
    const canvas = canvasRef.current;
    if (!wrap || !canvas) return;

    // `generation` restarts this effect: the Reset button.
    void generation;
    let renderer: Renderer | undefined;
    try {
      renderer = createRenderer(canvas, palette(wrap));
    } catch (error) {
      console.error('[demo]', error);
    }
    if (!renderer) {
      setStatus('error');
      setNoGl(true);
      return;
    }
    const gl = renderer;
    let runtime: DemoRuntime | undefined;
    let disposed = false;
    let frameId = 0;
    let visible = true;
    let width = 0;
    let height = 0;
    let frameStart = 0;
    let sparks = 0;
    let last = 0;
    let smoothMs = -1;
    let frames = 0;
    let fpsSince = 0;
    let fps = 0;
    let steps = 0;
    // The motion times of the last frames per group, [0] per entity and [1] batched: a single
    // time is near the browser's timer resolution, a mean over a window is not
    const samples = 128;
    const motion = [new Float64Array(samples), new Float64Array(samples)];
    const motionSum = [0, 0];
    let motionAt = 0;
    let motionCount = 0;

    function resize() {
      if (!wrap) return;
      width = Math.max(1, wrap.clientWidth);
      height = Math.max(1, wrap.clientHeight);
      gl.resize(
        width,
        height,
        Math.min(maxScale, window.devicePixelRatio || 1),
      );
      runtime?.exports.Resize(width, height);
      gl.present();
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
          // The host alternates the two motion groups, so each step updated one of them
          if (steps % 2 === 0) {
            for (let m = 0; m < 2; m++) {
              const t = runtime.exports.MotionMs(m === 1);
              motionSum[m] += t - motion[m][motionAt];
              motion[m][motionAt] = t;
            }
            motionAt = (motionAt + 1) % samples;
            motionCount = Math.min(samples, motionCount + 1);
          }
        }
        gl.draw(runtime.heap(), frameStart, sparks, dt);
        gl.present();
      }
      frames++;
      if (now - fpsSince >= 500) {
        fps = fpsSince ? (frames * 1000) / (now - fpsSince) : 0;
        frames = 0;
        fpsSince = now;
        setStats({
          entities: runtime.exports.EntityCount(),
          updateMs: Math.max(0, smoothMs),
          fps: pausedRef.current ? 0 : fps,
          batchMs: motionCount ? motionSum[1] / motionCount : 0,
          entityMs: motionCount ? motionSum[0] / motionCount : 0,
        });
      }
    }

    // The pointer is input: the world gets it as a singleton (Pointer) and AttractSystem reads it.
    // A mouse pulls while it hovers; a finger while it touches.
    function point(event: PointerEvent, active: boolean) {
      if (!runtime || !wrap) return;
      const box = wrap.getBoundingClientRect();
      runtime.exports.Point(
        event.clientX - box.left,
        event.clientY - box.top,
        active,
      );
    }
    const move = (event: PointerEvent) =>
      point(event, event.pointerType === 'mouse' || event.buttons > 0);
    const down = (event: PointerEvent) => point(event, true);
    const up = (event: PointerEvent) =>
      point(event, event.pointerType === 'mouse');
    const leave = (event: PointerEvent) => point(event, false);
    wrap.addEventListener('pointermove', move);
    wrap.addEventListener('pointerdown', down);
    wrap.addEventListener('pointerup', up);
    wrap.addEventListener('pointercancel', leave);
    wrap.addEventListener('pointerleave', leave);

    const resizeObserver = new ResizeObserver(resize);
    resizeObserver.observe(wrap);
    const visibility = new IntersectionObserver((entries) => {
      visible = entries.some((entry) => entry.isIntersecting);
      last = 0;
    });
    visibility.observe(wrap);
    // Fumadocs switches the theme with the `dark` class on <html>
    const theme = new MutationObserver(() => {
      gl.setPalette(palette(wrap));
      gl.present();
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
        // For the curious: which vector widths .NET runs as hardware SIMD in this browser
        const simd = loaded.exports.Simd();
        console.info(
          '[demo] hardware SIMD:',
          ['Vector128', 'Vector256', 'Vector512', 'Vector<T>']
            .filter((_, i) => simd & (1 << i))
            .join(', ') || 'none',
        );
        loaded.exports.Reset(count, width, height);
        // Reset may replace the frame array: its address is read after it
        frameStart = loaded.exports.FrameAddress() >> 2;
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
      wrap.removeEventListener('pointermove', move);
      wrap.removeEventListener('pointerdown', down);
      wrap.removeEventListener('pointerup', up);
      wrap.removeEventListener('pointercancel', leave);
      wrap.removeEventListener('pointerleave', leave);
      gl.dispose();
    };
  }, [count, generation]);

  const tool = button('secondary', 'sm');

  return (
    <div className={cn(surface, 'not-prose my-6 overflow-hidden')}>
      <div className="flex flex-wrap items-center gap-2 border-b p-3">
        {/* A segmented control, like Fumadocs' theme toggle: the pressed count is filled */}
        <fieldset className="inline-flex rounded-full border bg-fd-background p-0.5">
          <legend className="sr-only">{t.count}</legend>
          {counts.map((n) => (
            <button
              key={n}
              type="button"
              aria-pressed={count === n}
              onClick={() => setCount(n)}
              className={cn(
                'h-7 rounded-full px-3 text-[13px] tabular-nums transition-colors',
                count === n
                  ? 'bg-fd-accent font-medium text-fd-accent-foreground'
                  : 'text-fd-muted-foreground hover:text-fd-foreground',
              )}
            >
              {n >= 1000 ? `${n / 1000}k` : n}
            </button>
          ))}
        </fieldset>
        <button
          type="button"
          className={tool}
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
          className={tool}
          disabled={status !== 'running'}
          onClick={() => setGeneration((g) => g + 1)}
        >
          <RotateCcw className="size-3.5" />
          {t.reset}
        </button>
      </div>
      <div
        ref={wrapRef}
        className="relative h-[clamp(280px,60vw,480px)] w-full touch-pan-y select-none"
      >
        <canvas
          ref={canvasRef}
          className="absolute inset-0 size-full"
          role="img"
          aria-label={t.canvas}
        />
        {status === 'running' && (
          <p className="pointer-events-none absolute bottom-2 left-3 text-xs text-fd-muted-foreground">
            {t.hint}
          </p>
        )}
        {status !== 'running' && (
          <div className="absolute inset-0 flex items-center justify-center p-4 text-center text-sm text-fd-muted-foreground">
            {status === 'error' ? (noGl ? t.webgl : t.error) : t.loading}
            <noscript>{t.noscript}</noscript>
          </div>
        )}
      </div>
      <p className="border-t px-3 py-2 text-center text-sm text-fd-muted-foreground tabular-nums">
        {t.motion} {t.batched}{' '}
        <span className="font-medium text-fd-foreground">
          {stats.batchMs.toFixed(3)} ms
        </span>
        {' · '}
        {t.perEntity}{' '}
        <span className="font-medium text-fd-foreground">
          {stats.entityMs.toFixed(3)} ms
        </span>
        {stats.batchMs > 0 && (
          <>
            {' · '}
            <span className="font-medium text-fd-primary">
              {t.faster(`${(stats.entityMs / stats.batchMs).toFixed(1)}×`)}
            </span>
          </>
        )}
      </p>
      <dl className="grid grid-cols-3 divide-x border-t text-center">
        {[
          [t.entities, stats.entities.toLocaleString('en-US')],
          [t.update, stats.updateMs.toFixed(2)],
          [t.fps, stats.fps.toFixed(0)],
        ].map(([label, value]) => (
          // The value shows above its label; the markup keeps <dt> before <dd>
          <div key={label} className="flex flex-col-reverse px-2 py-3">
            <dt className="text-xs text-fd-muted-foreground">{label}</dt>
            <dd className="text-lg font-medium tracking-tight tabular-nums">
              {value}
            </dd>
          </div>
        ))}
      </dl>
    </div>
  );
}
