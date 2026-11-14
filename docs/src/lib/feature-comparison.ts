/**
 * The head-to-head comparisons of EasyEcs.Comparison, feature by feature:
 * what each measures, EasyEcs' time and every other library's time relative
 * to it. `featureComparison()` sorts them by EasyEcs' lead over the fastest
 * other library, straight from src/data/bench.json, so the order, the tiers
 * (a clear lead, close, behind) and every ratio follow the data. Shown by
 * <FeatureComparison /> and <LeadChart />, the home page, the social cards
 * and llms.txt.
 */
import { type BenchRow, benchRows, splitMethod } from './benchmarks';
import type { Locale } from './i18n';

type Text = Record<Locale, string>;

interface Matchup {
  /** The comparison class, as `benchClass` takes it */
  bench: string;
  params?: Record<string, string>;
  /** EasyEcs' rows, the fastest counts (default: every EasyEcs row) */
  ours?: string[];
  /** The other libraries' rows, the fastest of each library counts (default: every other row) */
  rivals?: string[];
  /** What is measured, for a table row */
  label: Text;
  /** A few words for a sentence: "bulk changes" */
  short: Text;
  /** The section of the comparison page that describes it */
  anchor: string;
  /** Why EasyEcs is behind here, shown only while the data says it is */
  why?: Text;
}

/**
 * Every head-to-head comparison: the same work done in each library its own
 * idiomatic way, with identical results checked in setup. The order here does
 * not matter; `featureComparison()` sorts by the data.
 */
const matchups: Matchup[] = [
  {
    bench: 'BulkChangeComparison',
    label: {
      en: 'Add a component to 100,000 entities by query, then remove it',
      zh: '按查询给 10 万个实体加上一个组件，再移除',
    },
    short: { en: 'bulk changes', zh: '批量增删组件' },
    anchor: 'bulk',
  },
  {
    bench: 'SaveComparison',
    label: {
      en: 'Save 10,000 entities and load them back',
      zh: '保存 1 万个实体再读回',
    },
    short: { en: 'saves', zh: '存档' },
    anchor: 'saves',
  },
  {
    bench: 'BulkSpawnComparison',
    label: {
      en: 'Spawn 10,000 entities beside 100,000, then destroy them',
      zh: '在 10 万实体旁生成 1 万个实体，再全部销毁',
    },
    short: { en: 'spawning', zh: '批量生成' },
    anchor: 'bulk',
  },
  // Creation in a fresh world, three components: reserved against reserved,
  // the bulk call against each library's fastest way, and one by one with
  // nothing reserved against the same in the others
  {
    bench: 'ColdCreateComparison',
    params: { Components: '3' },
    ours: ['EasyEcs_PerEntity_Reserved'],
    rivals: ['Friflo_PerEntity_Reserved', 'Arch_PerEntity_Reserved'],
    label: {
      en: 'Create 100,000 three-component entities one by one in a fresh world, room reserved first',
      zh: '在新 World 里先预留空间，再逐个创建 10 万个三组件实体',
    },
    short: { en: 'pre-sized creation', zh: '预留后创建' },
    anchor: 'cold-create',
  },
  {
    bench: 'ColdCreateComparison',
    params: { Components: '3' },
    ours: ['EasyEcs_Bulk'],
    label: {
      en: "Create 100,000 three-component entities in a fresh world in one call (the others' fastest way)",
      zh: '在新 World 里一次调用创建 10 万个三组件实体（对比其他库最快的写法）',
    },
    short: { en: 'bulk creation', zh: '批量创建' },
    anchor: 'cold-create',
  },
  {
    bench: 'ColdCreateComparison',
    params: { Components: '3' },
    ours: ['EasyEcs_PerEntity'],
    rivals: ['Friflo_PerEntity', 'Arch_PerEntity', 'DefaultEcs_PerEntity'],
    label: {
      en: 'Create 100,000 three-component entities one by one in a fresh world, nothing reserved',
      zh: '在新 World 里不预留空间，逐个创建 10 万个三组件实体',
    },
    short: {
      en: 'creation without pre-sizing',
      zh: '不预留的创建',
    },
    anchor: 'cold-create',
    why: {
      en: 'A fresh EasyEcs world grows its table and entity storage by doubling, and the arrays past 85,000 bytes land on the large object heap; together they pass the budget that sets off a full collection, once per new world (D28). Arch keeps entities in 16 KB chunks, which never reach that heap. Reserve the table with `Reserve<T1..Tn>(count)` or create the entities with `CreateEntities`, and EasyEcs is the fastest of the four.',
      zh: '新的 EasyEcs World 按倍数扩表和实体存储，超过 85,000 字节的数组进了大对象堆，累计越过预算，触发一次完整 GC，每个新 World 一次（D28）。Arch 把实体放在 16 KB 的块里，进不了大对象堆。先用 `Reserve<T1..Tn>(count)` 预留表，或者用 `CreateEntities` 批量创建，EasyEcs 就是四个库里最快的。',
    },
  },
  {
    bench: 'ValueQueryComparison',
    label: {
      en: 'Query 100,000 players by value: a rating range, zone sums',
      zh: '按值查询 10 万名玩家：评分区间、按区汇总',
    },
    short: { en: 'value queries', zh: '按值查询' },
    anchor: 'indexes-and-relations',
  },
  {
    bench: 'HierarchyComparison',
    label: {
      en: 'Create 1,000 parent-child squads, destroy them in cascade',
      zh: '创建 1,000 个父子小队，再级联销毁',
    },
    short: { en: 'hierarchies', zh: '层级' },
    anchor: 'hierarchy',
  },
  {
    bench: 'IndexComparison',
    params: { Trades: '1' },
    label: {
      en: 'Count 100 inventories through an index (1 item traded)',
      zh: '通过索引统计 100 个背包（1 件物品易手）',
    },
    short: { en: 'index lookups', zh: '索引查找' },
    anchor: 'indexes-and-relations',
  },
  {
    bench: 'IndexComparison',
    params: { Trades: '100' },
    label: {
      en: 'Count 100 inventories through an index (100 items traded)',
      zh: '通过索引统计 100 个背包（100 件物品易手）',
    },
    short: { en: 'index updates', zh: '索引更新' },
    anchor: 'indexes-and-relations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Toggle'],
    rivals: ['Friflo_Toggle'],
    label: {
      en: 'Add a status component to 5% of 100,000 entities, then remove it',
      zh: '给 10 万实体中的 5% 加上状态组件，再移除',
    },
    short: { en: 'toggling a status', zh: '切换状态' },
    anchor: 'operations',
  },
  {
    bench: 'ChangeTrackingComparison',
    params: { Written: '10' },
    ours: ['EasyEcs_Changed'],
    label: {
      en: 'Find the 10 of 100,000 entities written this tick',
      zh: '找出 10 万实体中本 tick 被写过的 10 个',
    },
    short: { en: 'change detection', zh: '变更检测' },
    anchor: 'change-tracking',
  },
  {
    bench: 'ChangeTrackingComparison',
    params: { Written: '1000' },
    ours: ['EasyEcs_Changed'],
    label: {
      en: 'Find the 1,000 of 100,000 entities written this tick',
      zh: '找出 10 万实体中本 tick 被写过的 1,000 个',
    },
    short: {
      en: 'change detection at 1,000 writes',
      zh: '1,000 次写入的变更检测',
    },
    anchor: 'change-tracking',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Instantiate'],
    rivals: ['Friflo_Instantiate'],
    label: {
      en: 'Spawn 5,000 entities from a prefab, then destroy them',
      zh: '用预制体生成 5,000 个实体，再销毁',
    },
    short: { en: 'prefabs', zh: '预制体' },
    anchor: 'operations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Iterate1'],
    rivals: ['Friflo_Iterate1'],
    label: {
      en: 'Iterate one component over 100,000 entities',
      zh: '遍历 10 万实体的一个组件',
    },
    short: { en: 'one-component iteration', zh: '单组件遍历' },
    anchor: 'operations',
  },
  {
    bench: 'DisableComparison',
    params: { Switched: '10000' },
    label: {
      en: 'Put 10,000 of 100,000 entities to sleep, wake the last ones',
      zh: '让 10 万实体中的 1 万个休眠，唤醒上一批',
    },
    short: { en: 'disabling', zh: '禁用' },
    anchor: 'disabling',
  },
  {
    bench: 'DisableComparison',
    params: { Switched: '100' },
    label: {
      en: 'Put 100 of 100,000 entities to sleep, wake the last ones',
      zh: '让 10 万实体中的 100 个休眠，唤醒上一批',
    },
    short: { en: 'disabling a few', zh: '少量禁用' },
    anchor: 'disabling',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_With'],
    rivals: ['Friflo_With'],
    label: {
      en: 'Iterate with a filter: the players among 100,000 entities',
      zh: '带过滤遍历：10 万实体中的玩家',
    },
    short: { en: 'filtered iteration', zh: '带过滤遍历' },
    anchor: 'operations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Entity'],
    rivals: ['Friflo_Entity'],
    label: {
      en: 'Iterate with the entity handle of each row',
      zh: '遍历时拿到每一行的实体句柄',
    },
    short: { en: 'iteration with the entity', zh: '带实体遍历' },
    anchor: 'operations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Iterate2Simd'],
    rivals: ['Friflo_Iterate2Simd'],
    label: {
      en: 'Move 100,000 entities with SIMD over spans',
      zh: '用 SIMD 按 span 移动 10 万个实体',
    },
    short: { en: 'SIMD iteration', zh: 'SIMD 遍历' },
    anchor: 'operations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_CreateDestroy'],
    rivals: ['Friflo_CreateDestroy'],
    label: {
      en: 'Create 5,000 entities one by one, then destroy them',
      zh: '逐个创建 5,000 个实体，再销毁',
    },
    short: { en: 'create and destroy', zh: '创建与销毁' },
    anchor: 'operations',
  },
  {
    bench: 'StructuralChangeComparison',
    params: { Killed: '1000' },
    label: {
      en: 'Add and remove a tag in the passes that find them (1,000 of 100,000)',
      zh: '在遍历途中增删标签（10 万中的 1,000 个）',
    },
    short: { en: 'structural changes', zh: '结构变更' },
    anchor: 'structural-changes',
  },
  {
    bench: 'StructuralChangeComparison',
    params: { Killed: '10' },
    label: {
      en: 'Add and remove a tag in the passes that find them (10 of 100,000)',
      zh: '在遍历途中增删标签（10 万中的 10 个）',
    },
    short: { en: 'a few structural changes', zh: '少量结构变更' },
    anchor: 'structural-changes',
    why: {
      en: 'Friflo defers the changes to its command buffer, so both of its passes are plain span loops. A `[ForEach]` that may change structure checks after every row that its table did not move, and over 100,000 rows that costs more than 10 deferred changes; from about 1,000 changes on, applying them at once is faster.',
      zh: 'Friflo 把变更推迟到命令缓冲，两次遍历都是普通的 span 循环。可能做结构变更的 `[ForEach]` 每处理一行都要确认表没有移动，10 万行下来，这比推迟 10 次变更的开销还大；变更到 1,000 次左右，立即生效反而更快。',
    },
  },
  {
    bench: 'RelationComparison',
    label: {
      en: 'Rewire 100 of 10,000 friend lists, walk 100 follower sets',
      zh: '改写 1 万份好友列表中的 100 份，遍历 100 组关注者',
    },
    short: { en: 'relations', zh: '关系' },
    anchor: 'indexes-and-relations',
  },
  {
    bench: 'ParallelSystemsComparison',
    params: { Parallel: 'False' },
    label: {
      en: 'Four independent systems over 100,000 entities, one after another',
      zh: '10 万实体上的 4 个独立系统，依次运行',
    },
    short: { en: 'systems', zh: '系统' },
    anchor: 'parallel',
  },
  {
    bench: 'ParallelSystemsComparison',
    params: { Parallel: 'True' },
    label: {
      en: 'Four independent systems over 100,000 entities, in parallel',
      zh: '10 万实体上的 4 个独立系统，并行运行',
    },
    short: { en: 'parallel systems', zh: '并行系统' },
    anchor: 'parallel',
  },
  {
    bench: 'EcsComparisonBenchmark',
    params: { Entities: '100000' },
    label: {
      en: 'The full server tick: nine systems over 100,000 entities',
      zh: '完整服务器 tick：10 万实体上的 9 个系统',
    },
    short: { en: 'the full server tick', zh: '完整服务器 tick' },
    anchor: 'the-same-server-tick',
  },
  {
    bench: 'EcsComparisonBenchmark',
    params: { Entities: '10000' },
    label: {
      en: 'The full server tick: nine systems over 10,000 entities',
      zh: '完整服务器 tick：1 万实体上的 9 个系统',
    },
    short: { en: 'the small server tick', zh: '小规模服务器 tick' },
    anchor: 'the-same-server-tick',
  },
  {
    bench: 'RemovalComparison',
    params: { Removed: '1000' },
    label: {
      en: 'Find the 1,000 of 100,000 entities that lost a component',
      zh: '找出 10 万实体中失去组件的 1,000 个',
    },
    short: { en: 'removal detection', zh: '移除检测' },
    anchor: 'removal-detection',
  },
  {
    bench: 'RemovalComparison',
    params: { Removed: '10' },
    label: {
      en: 'Find the 10 of 100,000 entities that lost a component',
      zh: '找出 10 万实体中失去组件的 10 个',
    },
    short: { en: 'a few removals', zh: '少量移除检测' },
    anchor: 'removal-detection',
    why: {
      en: "Friflo's `OnComponentRemoved` event pushes each id into a list you keep as the removal happens, a cost per removal; `[Removed<T>]` finds the removals through per-block summaries, a fixed cost per pass that 10 removals do not cover. From 1,000 removals on, EasyEcs leads, with no subscription to write.",
      zh: 'Friflo 的 `OnComponentRemoved` 事件在移除发生时把 id 推进你维护的列表，每次移除都要付一次；`[Removed<T>]` 通过按块的摘要找出移除，每次遍历是一笔固定开销，10 次移除摊不薄。到 1,000 次时 EasyEcs 领先，而且不用写订阅代码。',
    },
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Singleton'],
    rivals: ['Friflo_Singleton'],
    label: {
      en: 'Iterate two components and a singleton',
      zh: '遍历两个组件加一个单例',
    },
    short: { en: 'singletons', zh: '单例' },
    anchor: 'operations',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Iterate2'],
    rivals: ['Friflo_Iterate2'],
    label: {
      en: 'Move 100,000 entities: two components, per entity',
      zh: '移动 10 万个实体：两个组件，逐个实体',
    },
    short: { en: 'two-component iteration', zh: '双组件遍历' },
    anchor: 'operations',
  },
  {
    bench: 'ChangeTrackingComparison',
    params: { Written: '10' },
    ours: ['EasyEcs_Scan'],
    rivals: ['Friflo_Scan', 'Arch_Scan'],
    label: {
      en: 'Scan 100,000 entities for the 10 written, without a change filter',
      zh: '不用变更过滤，扫描 10 万实体找出被写过的 10 个',
    },
    short: { en: 'a full scan', zh: '全表扫描' },
    anchor: 'change-tracking',
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_Iterate3'],
    rivals: ['Friflo_Iterate3'],
    label: {
      en: 'Iterate three components over 100,000 entities',
      zh: '遍历 10 万实体的三个组件',
    },
    short: { en: 'three-component iteration', zh: '三组件遍历' },
    anchor: 'operations',
    why: {
      en: 'The same span loop over the same dense columns in both libraries: the loop is bound by memory, not by the library.',
      zh: '两个库在同样紧凑的列上跑同一个 span 循环：瓶颈在内存，不在库。',
    },
  },
  {
    bench: 'OperationBenchmark',
    ours: ['EasyEcs_EntityBatch'],
    rivals: ['Friflo_EntityBatch'],
    label: {
      en: 'Iterate in batches, reading entity ids only where needed',
      zh: '按批遍历，只在需要时读取实体 id',
    },
    short: { en: 'batched entity iteration', zh: '按批带实体遍历' },
    anchor: 'operations',
    why: {
      en: "The same loop over the same rows: Friflo's is written inline in the benchmark, EasyEcs' is generated from a batch `[ForEach]` method and runs as a system group.",
      zh: '同样的行上跑同样的循环：Friflo 的循环直接写在基准里，EasyEcs 的由批量 `[ForEach]` 方法生成，作为系统组运行。',
    },
  },
  {
    bench: 'ChangeTrackingComparison',
    params: { Written: '1000' },
    ours: ['EasyEcs_Scan'],
    rivals: ['Friflo_Scan', 'Arch_Scan'],
    label: {
      en: 'Scan 100,000 entities for the 1,000 written, without a change filter',
      zh: '不用变更过滤，扫描 10 万实体找出被写过的 1,000 个',
    },
    short: { en: 'a full scan after many writes', zh: '大量写入后的全表扫描' },
    anchor: 'change-tracking',
    why: {
      en: "Not the way to do it in EasyEcs: the component is `ITracked`, so each of the 1,000 writes also stamps its row's change tick, which `[Changed<T>]` needs and the scan ignores; Friflo's component is not tracked. `[Changed<T>]` does the same job faster than every library's scan.",
      zh: '在 EasyEcs 里不该这么写：组件实现了 `ITracked`，1,000 次写入每次都给所在行打上变更 tick，`[Changed<T>]` 要用它，扫描却用不上；Friflo 的组件没有追踪。同样的活，`[Changed<T>]` 比每个库的扫描都快。',
    },
  },
  {
    bench: 'ParallelQueryComparison',
    label: {
      en: 'Steer 100,000 agents on all cores (parallel query)',
      zh: '用全部核心为 10 万个智能体导航（并行查询）',
    },
    short: { en: 'parallel queries', zh: '并行查询' },
    anchor: 'parallel',
    why: {
      en: "Arch's and Friflo's runners keep worker threads of their own, which win when passes run back to back, as BenchmarkDotNet runs them. EasyEcs wakes thread-pool helpers only while they pay: in a real server tick a dedicated crew measured worse tail latency and more CPU per tick (D22).",
      zh: 'Arch 和 Friflo 的调度器各有自己的工作线程，像 BenchmarkDotNet 这样一轮接一轮地跑时占优。EasyEcs 只在划算时才唤醒线程池助手：在真实的服务器 tick 里，专用线程组的尾延迟更高、每个 tick 的 CPU 更多（D22）。',
    },
  },
];

/** EasyEcs leads clearly where the fastest other library takes at least this many times its time */
export const leadRatio = 1.25;

export type Tier = 'lead' | 'close' | 'behind';

export interface Rival {
  /** "Friflo", "Arch", "DefaultEcs" */
  library: string;
  /** The library's fastest row */
  row: BenchRow;
  /** Its time over EasyEcs' time */
  vs: number;
}

export interface FeatureResult {
  label: Text;
  short: Text;
  anchor: string;
  why?: Text;
  ours: BenchRow;
  /** The other libraries, fastest first */
  rivals: Rival[];
  /** The library compared against: the fastest other one, or the one asked for */
  versus: Rival;
  /** Its time over EasyEcs' time: above 1 when EasyEcs leads */
  ratio: number;
  /** The two means differ by less than their errors added up */
  withinError: boolean;
  tier: Tier;
}

const fastest = (rows: BenchRow[]) =>
  rows.reduce<BenchRow | undefined>(
    (best, row) => (!best || row.meanNs < best.meanNs ? row : best),
    undefined,
  );

const libraryOf = (row: BenchRow) => splitMethod(row.method).library;

interface Measured {
  matchup: Matchup;
  ours: BenchRow;
  rivals: Rival[];
}

function measure(m: Matchup): Measured | undefined {
  const rows = benchRows(m.bench, m.params);
  const ours = fastest(
    rows.filter((r) =>
      m.ours ? m.ours.includes(r.method) : libraryOf(r) === 'EasyEcs',
    ),
  );
  if (!ours) return undefined;
  const others = rows.filter((r) =>
    m.rivals ? m.rivals.includes(r.method) : libraryOf(r) !== 'EasyEcs',
  );
  const rivals: Rival[] = [];
  for (const library of new Set(others.map(libraryOf))) {
    const row = fastest(others.filter((r) => libraryOf(r) === library));
    if (row) rivals.push({ library, row, vs: row.meanNs / ours.meanNs });
  }
  rivals.sort((a, b) => a.vs - b.vs);
  return rivals.length ? { matchup: m, ours, rivals } : undefined;
}

/** One comparison against one library: the ratio, the error test and the tier */
function against(
  { matchup: m, ours, rivals }: Measured,
  versus: Rival,
): FeatureResult {
  const ratio = versus.vs;
  return {
    label: m.label,
    short: m.short,
    anchor: m.anchor,
    why: m.why,
    ours,
    rivals,
    versus,
    ratio,
    withinError:
      Math.abs(versus.row.meanNs - ours.meanNs) <=
      (versus.row.errorNs ?? 0) + (ours.errorNs ?? 0),
    // 0.99x reads as behind, 1.00x as even
    tier: ratio >= leadRatio ? 'lead' : ratio < 0.995 ? 'behind' : 'close',
  };
}

const measured = matchups.flatMap((m) => measure(m) ?? []);

const byLead = (a: FeatureResult, b: FeatureResult) => b.ratio - a.ratio;

/** Against the fastest other library */
const all = measured.map((m) => against(m, m.rivals[0])).sort(byLead);

/**
 * Every head-to-head comparison, the biggest lead first, or those of one
 * tier: a clear lead (the other library takes at least `leadRatio` times
 * EasyEcs' time), close, or behind. Against the fastest other library, or
 * against `library` ("Friflo", "Arch", "DefaultEcs") in the comparisons it
 * takes part in.
 */
export function featureComparison(
  tier?: Tier,
  library?: string,
): FeatureResult[] {
  const results = library
    ? measured
        .flatMap((m) => {
          const versus = m.rivals.find((r) => r.library === library);
          return versus ? [against(m, versus)] : [];
        })
        .sort(byLead)
    : all;
  return tier ? results.filter((r) => r.tier === tier) : results;
}

/**
 * How many comparisons there are, how many EasyEcs is faster or slower in by
 * more than the measurement error (`ahead`, `slower`) or within it (`even`),
 * and how many each tier holds; against the fastest other library, or
 * against `library`
 */
export function featureCounts(library?: string) {
  const results = featureComparison(undefined, library);
  const count = (test: (r: FeatureResult) => boolean) =>
    results.filter(test).length;
  return {
    total: results.length,
    ahead: count((r) => r.ratio > 1 && !r.withinError),
    even: count((r) => r.withinError),
    slower: count((r) => r.ratio < 1 && !r.withinError),
    lead: count((r) => r.tier === 'lead'),
    close: count((r) => r.tier === 'close'),
    behind: count((r) => r.tier === 'behind'),
  };
}
