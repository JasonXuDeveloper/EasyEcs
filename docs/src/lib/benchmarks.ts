/**
 * Every benchmark number the site shows (home page, MDX through the bench
 * components, llms.txt, social copy). The data is src/data/bench.json,
 * written by scripts/sync-benchmarks.ts from BenchmarkDotNet's
 * `*-report.csv` files; nothing here or in MDX types a number in by hand.
 *
 * A number is addressed by benchmark class, method and [Params] values:
 * `bench('WorldSimulationBenchmark', 'Tick', { Entities: '100000' })`, or by
 * a named key (`keys` below) that MDX uses: `<BenchValue k="tick.oneWorld" />`.
 */
import data from '@/data/bench.json';

export interface BenchRow {
  method: string;
  params: Record<string, string>;
  meanNs: number;
  errorNs: number | null;
  stdDevNs: number | null;
  ratio: number | null;
  allocatedBytes: number | null;
}

interface BenchClass {
  namespace: string;
  job: string | null;
  rows: BenchRow[];
}

const benchmarks = data.benchmarks as unknown as Record<string, BenchClass>;
export const environment = data.environment;
export const peerVersions = data.peers as Record<string, string>;
export const benchGeneratedAt = data.generatedAt;

/**
 * A class by its name ("RemovalComparison") or by the end of its full name
 * ("Features.RemovalComparison", "EcsComparison.Features.RemovalComparison").
 */
export function benchClass(name: string): BenchClass | undefined {
  if (benchmarks[name]) return benchmarks[name];
  for (const [short, entry] of Object.entries(benchmarks)) {
    const full = entry.namespace ? `${entry.namespace}.${short}` : short;
    if (full === name || full.endsWith(`.${name}`)) return entry;
  }
  return undefined;
}

/** The rows of a class whose [Params] include every given value */
export function benchRows(name: string, params: Record<string, string> = {}) {
  const rows = benchClass(name)?.rows ?? [];
  return rows.filter((row) =>
    Object.entries(params).every(([k, v]) => row.params[k] === v),
  );
}

export function bench(
  name: string,
  method: string,
  params: Record<string, string> = {},
): BenchRow | undefined {
  return benchRows(name, params).find((row) => row.method === method);
}

/* ------------------------------------------------------------------ */
/* Formatting                                                          */
/* ------------------------------------------------------------------ */

const digits = (value: number) =>
  value >= 100
    ? value.toFixed(0)
    : value >= 10
      ? value.toFixed(1)
      : value.toFixed(2);

/** 1753900 -> "1.75 ms", 8751 -> "8.75 μs", 0.875 -> "0.88 ns" */
export function formatTime(ns: number) {
  if (ns >= 1e9) return `${digits(ns / 1e9)} s`;
  if (ns >= 1e6) return `${digits(ns / 1e6)} ms`;
  if (ns >= 1e3) return `${digits(ns / 1e3)} μs`;
  return `${digits(ns)} ns`;
}

/** 0 -> "0 B", 67224 -> "65.6 KB", 550526 -> "538 KB" */
export function formatBytes(bytes: number | null) {
  if (bytes === null) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${digits(bytes / 1024)} KB`;
  return `${digits(bytes / 1024 ** 2)} MB`;
}

export function formatRatio(ratio: number) {
  return `${ratio >= 10 ? ratio.toFixed(0) : ratio.toFixed(ratio >= 3 ? 1 : 2)}×`;
}

/* ------------------------------------------------------------------ */
/* Named values                                                        */
/* ------------------------------------------------------------------ */

export type Field = 'mean' | 'allocated' | 'ratio' | 'perEntity';

interface KeyDef {
  bench: string;
  method: string;
  params?: Record<string, string>;
  field?: Field;
  /** Entities per operation, for `perEntity` */
  per?: number;
  /** A ratio against another row of the same class: this row / that row */
  over?: { method: string; params?: Record<string, string> };
}

const N100k = { Entities: '100000' };
const C3 = { Components: '3' };

/**
 * The named numbers MDX and the home page use. A key names what it measures,
 * not its value, so it stays right when the data is regenerated.
 */
export const keys: Record<string, KeyDef> = {
  'tick.oneWorld': {
    bench: 'WorldSimulationBenchmark',
    method: 'Tick',
    params: N100k,
  },
  'tick.oneWorld.alloc': {
    bench: 'WorldSimulationBenchmark',
    method: 'Tick',
    params: N100k,
    field: 'allocated',
  },
  'tick.oneWorld10k': {
    bench: 'WorldSimulationBenchmark',
    method: 'Tick',
    params: { Entities: '10000' },
  },
  'tick.fourWorlds': {
    bench: 'MultiWorldBenchmark',
    method: 'TickAllWorlds',
    params: { TotalEntities: '100000' },
  },
  'tick.battle': {
    bench: 'BattleServerBenchmark',
    method: 'Tick',
    params: { Units: '100000' },
  },
  'forEach.perEntity': {
    bench: 'MicroBenchmarks',
    method: 'ForEachSystemWrite',
    params: { N: '10000' },
    field: 'perEntity',
    per: 10000,
  },
  'rawArray.perEntity': {
    bench: 'MicroBenchmarks',
    method: 'RawArrayWrite',
    params: { N: '10000' },
    field: 'perEntity',
    per: 10000,
  },
  'peers.easyecs': {
    bench: 'EcsComparisonBenchmark',
    method: 'EasyEcs',
    params: N100k,
  },
  'peers.friflo': {
    bench: 'EcsComparisonBenchmark',
    method: 'Friflo',
    params: N100k,
  },
  'peers.arch': {
    bench: 'EcsComparisonBenchmark',
    method: 'Arch',
    params: N100k,
  },
  'peers.defaultecs': {
    bench: 'EcsComparisonBenchmark',
    method: 'DefaultEcs',
    params: N100k,
  },
  'peers.arch.ratio': {
    bench: 'EcsComparisonBenchmark',
    method: 'Arch',
    params: N100k,
    field: 'ratio',
  },
  'peers.defaultecs.ratio': {
    bench: 'EcsComparisonBenchmark',
    method: 'DefaultEcs',
    params: N100k,
    field: 'ratio',
  },
  // Change tracking: one system over 100,000 rows of which `Written` were
  // written since its last run, with and without [Changed<T>]
  'changed.10': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '10', Changed: 'True' },
  },
  'changed.10.scan': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '10', Changed: 'False' },
  },
  'changed.10.speedup': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '10', Changed: 'False' },
    field: 'ratio',
    over: { method: 'Tick', params: { Written: '10', Changed: 'True' } },
  },
  'changed.1000': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '1000', Changed: 'True' },
  },
  'changed.1000.scan': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '1000', Changed: 'False' },
  },
  'changed.1000.speedup': {
    bench: 'ChangeTrackingBenchmark',
    method: 'Tick',
    params: { Written: '1000', Changed: 'False' },
    field: 'ratio',
    over: { method: 'Tick', params: { Written: '1000', Changed: 'True' } },
  },
  'changed.query.10': {
    bench: 'ChangeTrackingBenchmark',
    method: 'QueryTick',
    params: { Written: '10', Changed: 'True' },
  },
  'removed.10': {
    bench: 'RemovalComparison',
    method: 'EasyEcs_Removed',
    params: { Removed: '10' },
  },
  'removed.1000': {
    bench: 'RemovalComparison',
    method: 'EasyEcs_Removed',
    params: { Removed: '1000' },
  },
  // Performance, why-fast and the comparison pages
  'tick.oneWorld10k.alloc': {
    bench: 'WorldSimulationBenchmark',
    method: 'Tick',
    params: { Entities: '10000' },
    field: 'allocated',
  },
  'tick.fourWorlds.alloc': {
    bench: 'MultiWorldBenchmark',
    method: 'TickAllWorlds',
    params: { TotalEntities: '100000' },
    field: 'allocated',
  },
  'tick.battle.alloc': {
    bench: 'BattleServerBenchmark',
    method: 'Tick',
    params: { Units: '100000' },
    field: 'allocated',
  },
  'peers.easyecs.alloc': {
    bench: 'EcsComparisonBenchmark',
    method: 'EasyEcs',
    params: { Entities: '100000' },
    field: 'allocated',
  },
  'peers.friflo.ratio': {
    bench: 'EcsComparisonBenchmark',
    method: 'Friflo',
    params: { Entities: '100000' },
    field: 'ratio',
  },
  'peers.arch.alloc': {
    bench: 'EcsComparisonBenchmark',
    method: 'Arch',
    params: { Entities: '100000' },
    field: 'allocated',
  },
  'peers.defaultecs.alloc': {
    bench: 'EcsComparisonBenchmark',
    method: 'DefaultEcs',
    params: { Entities: '100000' },
    field: 'allocated',
  },
  'peers.easyecs10k': {
    bench: 'EcsComparisonBenchmark',
    method: 'EasyEcs',
    params: { Entities: '10000' },
  },
  'peers.friflo10k.ratio': {
    bench: 'EcsComparisonBenchmark',
    method: 'Friflo',
    params: { Entities: '10000' },
    field: 'ratio',
  },
  'ops.iterate1': { bench: 'OperationBenchmark', method: 'EasyEcs_Iterate1' },
  'ops.iterate1.friflo': {
    bench: 'OperationBenchmark',
    method: 'Friflo_Iterate1',
  },
  'ops.iterate2': { bench: 'OperationBenchmark', method: 'EasyEcs_Iterate2' },
  'ops.iterate2Simd': {
    bench: 'OperationBenchmark',
    method: 'EasyEcs_Iterate2Simd',
  },
  'ops.toggle': { bench: 'OperationBenchmark', method: 'EasyEcs_Toggle' },
  'ops.toggle.friflo': { bench: 'OperationBenchmark', method: 'Friflo_Toggle' },
  'ops.instantiate': {
    bench: 'OperationBenchmark',
    method: 'EasyEcs_Instantiate',
  },
  'ops.instantiate.friflo': {
    bench: 'OperationBenchmark',
    method: 'Friflo_Instantiate',
  },
  'micro.queryIterate': {
    bench: 'MicroBenchmarks',
    method: 'QueryIterate',
    params: { N: '10000' },
  },
  'micro.forEachIterate': {
    bench: 'MicroBenchmarks',
    method: 'ForEachIterate',
    params: { N: '10000' },
  },
  'micro.churn': {
    bench: 'MicroBenchmarks',
    method: 'Churn',
    params: { N: '10000' },
  },
  'micro.churnBatch': {
    bench: 'MicroBenchmarks',
    method: 'ChurnBatch',
    params: { N: '10000' },
  },
  'micro.toggle': {
    bench: 'MicroBenchmarks',
    method: 'ToggleComponent',
    params: { N: '10000' },
  },
  'micro.forEachSystemWrite': {
    bench: 'MicroBenchmarks',
    method: 'ForEachSystemWrite',
    params: { N: '10000' },
  },
  'micro.rawArrayWrite': {
    bench: 'MicroBenchmarks',
    method: 'RawArrayWrite',
    params: { N: '10000' },
  },
  'parallel.forEach.off': {
    bench: 'ParallelForEachBenchmark',
    method: 'Tick',
    params: { Parallel: 'False' },
  },
  'parallel.forEach.on': {
    bench: 'ParallelForEachBenchmark',
    method: 'Tick',
    params: { Parallel: 'True' },
  },
  'parallel.systems.off': {
    bench: 'ParallelSystemsBenchmark',
    method: 'Tick',
    params: { Parallel: 'False' },
  },
  'parallel.systems.on': {
    bench: 'ParallelSystemsBenchmark',
    method: 'Tick',
    params: { Parallel: 'True' },
  },
  'count.tested': { bench: 'CountBenchmark', method: 'Count' },
  'sparse.passFew': { bench: 'SparseBenchmark', method: 'PassOverTheFew' },
  'sparse.countFew': { bench: 'SparseBenchmark', method: 'CountTheFew' },
  'sparse.passRest': { bench: 'SparseBenchmark', method: 'PassOverTheRest' },
  'bulk.setAll': { bench: 'BulkBenchmark', method: 'StunAndLift' },
  'bulk.each': { bench: 'BulkBenchmark', method: 'StunAndLiftEach' },
  'callsite.once': { bench: 'CallSiteBenchmark', method: 'FilterOnce' },
  'callsite.atCall': { bench: 'CallSiteBenchmark', method: 'FilterAtCall' },
  'removed.friflo.10': {
    bench: 'RemovalComparison',
    method: 'Friflo_Event',
    params: { Removed: '10' },
  },
  'removed.friflo.1000': {
    bench: 'RemovalComparison',
    method: 'Friflo_Event',
    params: { Removed: '1000' },
  },
  'relation.easyecs': {
    bench: 'RelationComparison',
    method: 'EasyEcs_Relations',
  },
  'relation.friflo': {
    bench: 'RelationComparison',
    method: 'Friflo_LinkRelations',
  },
  'parallelQuery.easyecs': {
    bench: 'ParallelQueryComparison',
    method: 'EasyEcs_Parallel',
  },
  'parallelQuery.friflo': {
    bench: 'ParallelQueryComparison',
    method: 'Friflo_QueryJob',
  },
  'maybe.one': { bench: 'MaybeBenchmark', method: 'OneMethod' },
  'maybe.two': { bench: 'MaybeBenchmark', method: 'TwoMethods' },
  'runner.runner': { bench: 'RunnerBenchmark', method: 'Runner' },
  'runner.query': { bench: 'RunnerBenchmark', method: 'Query' },
  'prefab.each': { bench: 'PrefabBenchmark', method: 'InstantiateEach' },
  'prefab.batch': { bench: 'PrefabBenchmark', method: 'InstantiateBatch' },
  'snapshot.capture.alloc': {
    bench: 'SnapshotBenchmark',
    method: 'Capture',
    field: 'allocated',
  },
  // Creating 100,000 entities of three components in a fresh world: one by
  // one, one by one after Reserve<T1..Tn>, and in one CreateEntities call
  'cold.perEntity': {
    bench: 'ColdCreateComparison',
    method: 'EasyEcs_PerEntity',
    params: C3,
  },
  'cold.reserved': {
    bench: 'ColdCreateComparison',
    method: 'EasyEcs_PerEntity_Reserved',
    params: C3,
  },
  'cold.bulk': {
    bench: 'ColdCreateComparison',
    method: 'EasyEcs_Bulk',
    params: C3,
  },
  /** How many times longer creation takes without the reservation */
  'cold.reserved.speedup': {
    bench: 'ColdCreateComparison',
    method: 'EasyEcs_PerEntity',
    params: C3,
    field: 'ratio',
    over: { method: 'EasyEcs_PerEntity_Reserved', params: C3 },
  },
  'cold.perEntity.arch': {
    bench: 'ColdCreateComparison',
    method: 'Arch_PerEntity',
    params: C3,
  },
  /** EasyEcs' time over Arch's, one by one with nothing reserved */
  'cold.perEntity.overArch': {
    bench: 'ColdCreateComparison',
    method: 'EasyEcs_PerEntity',
    params: C3,
    field: 'ratio',
    over: { method: 'Arch_PerEntity', params: C3 },
  },
  'cold.reserved.arch': {
    bench: 'ColdCreateComparison',
    method: 'Arch_PerEntity_Reserved',
    params: C3,
  },
  /** Arch's time over EasyEcs', both reserved */
  'cold.reserved.arch.ratio': {
    bench: 'ColdCreateComparison',
    method: 'Arch_PerEntity_Reserved',
    params: C3,
    field: 'ratio',
    over: { method: 'EasyEcs_PerEntity_Reserved', params: C3 },
  },
};

export interface BenchNumber {
  text: string;
  value: number;
  row: BenchRow;
}

/** A named number, or undefined when the data has no such row */
export function benchNumber(key: string): BenchNumber | undefined {
  const def = keys[key];
  if (!def) return undefined;
  const row = bench(def.bench, def.method, def.params);
  if (!row) return undefined;
  switch (def.field ?? 'mean') {
    case 'allocated':
      return row.allocatedBytes === null
        ? undefined
        : {
            text: formatBytes(row.allocatedBytes),
            value: row.allocatedBytes,
            row,
          };
    case 'perEntity': {
      const ns = row.meanNs / (def.per ?? 1);
      return { text: formatTime(ns), value: ns, row };
    }
    case 'ratio': {
      const base = def.over
        ? bench(def.bench, def.over.method, def.over.params)?.meanNs
        : undefined;
      const ratio = def.over ? (base ? row.meanNs / base : null) : row.ratio;
      return ratio === null
        ? undefined
        : { text: formatRatio(ratio), value: ratio, row };
    }
    default:
      return { text: formatTime(row.meanNs), value: row.meanNs, row };
  }
}

/** The text of a named number; throws on an unknown key so a typo fails the build */
export function benchValue(key: string) {
  if (!(key in keys)) throw new Error(`Unknown benchmark key "${key}"`);
  return benchNumber(key)?.text ?? '—';
}

/* ------------------------------------------------------------------ */
/* Peers                                                               */
/* ------------------------------------------------------------------ */

export interface Peer {
  method: string;
  name: string;
  ours: boolean;
  row: BenchRow;
}

const peerNames: Record<string, { name: string; pkg?: string }> = {
  EasyEcs: { name: 'EasyEcs' },
  Friflo: { name: 'Friflo.Engine.ECS', pkg: 'Friflo.Engine.ECS' },
  Arch: { name: 'Arch', pkg: 'Arch' },
  DefaultEcs: { name: 'DefaultEcs', pkg: 'DefaultEcs' },
};

/** "Friflo.Engine.ECS 3.6" from the comparison project's package versions */
export function peerLabel(method: string, ourVersion: string) {
  const peer = peerNames[method];
  if (!peer) return method;
  const version = peer.pkg ? peerVersions[peer.pkg] : ourVersion;
  return version
    ? `${peer.name} ${version.split('.').slice(0, 2).join('.')}`
    : peer.name;
}

/** The headline comparison (the same server tick ported to each library), fastest first */
export function peers(entities = '100000'): Peer[] {
  return benchRows('EcsComparisonBenchmark', { Entities: entities })
    .map((row) => ({
      method: row.method,
      name: peerNames[row.method]?.name ?? row.method,
      ours: row.method === 'EasyEcs',
      row,
    }))
    .sort((a, b) => a.row.meanNs - b.row.meanNs);
}

/* ------------------------------------------------------------------ */
/* Comparisons (BenchChart / BenchTable)                               */
/* ------------------------------------------------------------------ */

export interface ComparisonRow {
  row: BenchRow;
  /** "Friflo" of "Friflo_DirtyTag"; the whole method when it has no "_" */
  library: string;
  /** "DirtyTag" of "Friflo_DirtyTag"; "" when the method is the library */
  case: string;
  ours: boolean;
  /**
   * A row of EasyEcs' own benchmarks (EasyEcs.Benchmark): every row is EasyEcs,
   * the case is the method and `vsOurs` is BenchmarkDotNet's ratio against the
   * class's baseline (null when the class has none)
   */
  own: boolean;
  /** This row's time over EasyEcs' time for the same work (1 for that row) */
  vsOurs: number | null;
}

export interface Comparison {
  rows: ComparisonRow[];
  /** [Params] that differ between the rows (shown next to each label) */
  varying: string[];
  /**
   * The libraries run the same cases (OperationBenchmark: EasyEcs_Iterate1
   * and Friflo_Iterate1): the rows are grouped by case, one bar per library.
   * A case only EasyEcs has (ColdCreateComparison's Bulk) is a group of one
   * bar, beside the cases the others share.
   */
  grouped: boolean;
  /** A class of EasyEcs.Benchmark: EasyEcs against itself, ratios against the baseline */
  own: boolean;
}

export interface ComparisonQuery {
  bench: string;
  /** [Params] filter, "Written=1000" or "Removed=10,Mode=A" */
  params?: string;
  /** Methods to keep, comma-separated, in this order (default: all) */
  methods?: string;
}

/** "a=1,b=2" -> { a: "1", b: "2" } (MDX props are strings) */
export function parseParams(params?: string) {
  const out: Record<string, string> = {};
  for (const pair of params?.split(',') ?? []) {
    const [k, v] = pair.split('=').map((s) => s.trim());
    if (k && v !== undefined) out[k] = v;
  }
  return out;
}

/** "Friflo_DirtyTag" -> { library: "Friflo", case: "DirtyTag" } */
export const splitMethod = (method: string) => {
  const at = method.indexOf('_');
  return at < 0
    ? { library: method, case: '' }
    : { library: method.slice(0, at), case: method.slice(at + 1) };
};

/**
 * The rows of one benchmark class, each with its time relative to EasyEcs.
 * The EasyEcs row a row is measured against has the same [Params]: in a
 * grouped class the same case (Friflo_Iterate1 against EasyEcs_Iterate1),
 * otherwise the first EasyEcs row, which is the class's
 * [Benchmark(Baseline = true)] in every comparison (so this matches
 * BenchmarkDotNet's Ratio column).
 */
export function comparison({
  bench,
  params,
  methods,
}: ComparisonQuery): Comparison {
  const filter = parseParams(params);
  let rows = benchRows(bench, filter);
  if (methods) {
    const all = rows;
    rows = methods
      .split(',')
      .map((m) => m.trim())
      .flatMap((m) => all.filter((row) => row.method === m));
  }
  const keyOf = (row: BenchRow) => JSON.stringify(row.params);
  // EasyEcs' own benchmarks compare ways of doing one job in EasyEcs, not libraries
  const own = benchClass(bench)?.namespace === 'EasyEcs.Benchmark';
  const items = rows.map((row) => {
    const { library, case: name } = own
      ? { library: 'EasyEcs', case: row.method }
      : splitMethod(row.method);
    return { row, library, case: name, ours: library === 'EasyEcs', own };
  });
  const cases = new Set(items.map((r) => r.case));
  const grouped =
    items.length > 1 &&
    !cases.has('') &&
    cases.size < items.length &&
    items.some((r) => !r.ours) &&
    [...cases].every(
      (c) => items.filter((r) => r.case === c && r.ours).length === 1,
    );
  const result: ComparisonRow[] = items.map((item) => {
    const same = items.filter(
      (o) => o.ours && keyOf(o.row) === keyOf(item.row),
    );
    const base = grouped ? same.find((o) => o.case === item.case) : same[0];
    return {
      ...item,
      vsOurs: own
        ? item.row.ratio
        : base
          ? item.row.meanNs / base.row.meanNs
          : null,
    };
  });
  const varying = Object.keys(rows[0]?.params ?? {}).filter(
    (k) => !(k in filter) && new Set(rows.map((r) => r.params[k])).size > 1,
  );
  if (grouped) {
    const order = [...cases];
    result.sort(
      (a, b) =>
        order.indexOf(a.case) - order.indexOf(b.case) ||
        Number(b.ours) - Number(a.ours),
    );
  }
  return { rows: result, varying, grouped, own };
}

/** "Friflo.Engine.ECS" for "Friflo" */
export function libraryName(library: string) {
  return peerNames[library]?.name ?? library;
}

/** A row's label: "Friflo.Engine.ECS: DirtyTag (Written=10)" */
export function comparisonLabel(
  item: ComparisonRow,
  varying: string[],
  withLibrary = true,
) {
  const parts = [
    withLibrary && !item.own ? libraryName(item.library) : '',
    item.case,
  ].filter(Boolean);
  const params = varying.map((k) => `${k}=${item.row.params[k]}`).join(', ');
  return `${parts.join(': ')}${params ? ` (${params})` : ''}`;
}

/** One line naming the machine and the tool, under every chart and table */
export function sourceLine(zh: boolean) {
  const env = environment;
  const cores = env.cores?.match(/(\d+) physical/)?.[1];
  const os = env.os?.replace(/^Linux\s+/, '').replace(/\s*\(.*\)$/, '');
  const machine = [
    env.cpu,
    cores ? (zh ? `${cores} 核` : `${cores} cores`) : null,
    os,
  ]
    .filter(Boolean)
    .join(zh ? '，' : ', ');
  return zh
    ? `BenchmarkDotNet ${env.benchmarkDotNet}，${env.runtime}，${machine}。`
    : `BenchmarkDotNet ${env.benchmarkDotNet}, ${env.runtime}, ${machine}.`;
}
