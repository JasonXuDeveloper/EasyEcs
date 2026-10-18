/**
 * Loads the demo's .NET runtime (public/demo/_framework, published from
 * demo/EasyEcs.Demo by scripts/build-demo.sh) once per page load, on first
 * use. The C# side is EasyEcs.Demo.Demo: every call passes a few numbers, and
 * the sparks come back through one float array in the WebAssembly memory
 * (x, y, heat, light per spark), read in place without copying.
 */

/** The [JSExport] methods of demo/EasyEcs.Demo/Demo.cs */
export interface DemoExports {
  Reset(count: number, width: number, height: number): void;
  Resize(width: number, height: number): void;
  Point(x: number, y: number, active: boolean): void;
  MotionMs(batched: boolean): number;
  Simd(): number;
  Step(dt: number): number;
  FrameAddress(): number;
  UpdateMs(): number;
  EntityCount(): number;
}

export interface DemoRuntime {
  exports: DemoExports;
  /** The current view of the WebAssembly memory (replaced when it grows) */
  heap(): Float32Array;
}

interface DotnetRuntime {
  getAssemblyExports(name: string): Promise<{
    EasyEcs: { Demo: { Demo: DemoExports } };
  }>;
  getConfig(): { mainAssemblyName?: string };
  localHeapViewF32(): Float32Array;
  runMain(): Promise<number>;
}

interface DotnetModule {
  dotnet: { create(): Promise<DotnetRuntime> };
}

const runtimeUrl = '/demo/_framework/dotnet.js';
let loading: Promise<DemoRuntime> | undefined;

async function load(): Promise<DemoRuntime> {
  const url = new URL(runtimeUrl, window.location.origin).href;
  const { dotnet } = (await import(/* @vite-ignore */ url)) as DotnetModule;
  const runtime = await dotnet.create();
  await runtime.runMain();
  const name = runtime.getConfig().mainAssemblyName ?? 'EasyEcs.Demo';
  const assembly = await runtime.getAssemblyExports(name);
  return {
    exports: assembly.EasyEcs.Demo.Demo,
    heap: () => runtime.localHeapViewF32(),
  };
}

/** The runtime of this page: started by the first caller, shared after that. */
export function loadDemoRuntime(): Promise<DemoRuntime> {
  loading ??= load().catch((error: unknown) => {
    loading = undefined;
    throw error;
  });
  return loading;
}
