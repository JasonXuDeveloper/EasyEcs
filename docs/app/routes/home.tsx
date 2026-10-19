import { getHighlighter } from 'fumadocs-core/highlight';
import { Card, Cards } from 'fumadocs-ui/components/card';
import { CodeBlock, Pre } from 'fumadocs-ui/components/codeblock';
import { HomeLayout } from 'fumadocs-ui/layouts/home';
import {
  ArrowRight,
  BookOpen,
  Bot,
  Check,
  ChevronDown,
  Cpu,
  History,
  Layers,
  type LucideIcon,
  Network,
  Radar,
  ShieldCheck,
  Sparkles,
  Stamp,
  Zap,
} from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router';
import {
  type BenchNumber,
  bench,
  benchNumber,
  formatBytes,
  formatRatio,
  formatTime,
  libraryName,
  peerLabel,
  peers,
  sourceLine,
} from '@/lib/benchmarks';
import { cn } from '@/lib/cn';
import {
  type FeatureResult,
  featureComparison,
  featureCounts,
} from '@/lib/feature-comparison';
import { getLocale, type Locale } from '@/lib/i18n';
import { baseOptions } from '@/lib/layout.shared';
import { homeMeta } from '@/lib/seo';
import { docsVersion, githubUrl, installCommand } from '@/lib/site';
import { button, surface } from '@/lib/ui';
import type { Route } from './+types/home';

/* ------------------------------------------------------------------ */
/* Numbers (src/data/bench.json through src/lib/benchmarks.ts)         */
/* ------------------------------------------------------------------ */

const num = (key: string) => benchNumber(key);
const text = (n: BenchNumber | undefined) => n?.text ?? '—';

const oneWorld = num('tick.oneWorld');
const fourWorlds = num('tick.fourWorlds');
const battle = num('tick.battle');
const perEntity = num('forEach.perEntity');
const rawArray = num('rawArray.perEntity');
const tickAlloc = num('tick.oneWorld.alloc');
const zeroAlloc = tickAlloc?.value === 0;

/** The peers of the headline tick, fastest first */
const peerList = peers();
/** EasyEcs' own row of the headline tick: what the headline's allocation figure quotes */
const oursPeer = peerList.find((p) => p.ours);
/** "0 B": the memory one headline tick allocates */
const tickBytes = (
  oursPeer ? formatBytes(oursPeer.row.allocatedBytes) : text(tickAlloc)
).replace(' ', '\u00a0');
/** "1.68 ms": EasyEcs' time for the headline tick */
const tickTime = oursPeer ? formatTime(oursPeer.row.meanNs) : text(oneWorld);
const slowerPeers = peerList.filter((p) => !p.ours && (p.row.ratio ?? 0) >= 2);
const slowRange = slowerPeers.length
  ? `${formatRatio(Math.min(...slowerPeers.map((p) => p.row.ratio ?? 0)))}–${formatRatio(Math.max(...slowerPeers.map((p) => p.row.ratio ?? 0)))}`
  : '';
/** "EasyEcs 4.1 1.79 ms, Friflo.Engine.ECS 3.6 1.96 ms, ..." */
const peerTimes = (zh: boolean) =>
  peerList
    .map(
      (p) => `${peerLabel(p.method, docsVersion)} ${formatTime(p.row.meanNs)}`,
    )
    .join(zh ? '，' : ', ');

/** The head-to-head comparisons, the biggest lead first (src/lib/feature-comparison.ts) */
const leads = featureComparison('lead');
const counts = featureCounts();
/** "30× faster saves": a lead in a few words, for the headline and the description */
const leadPhrase = (i: number, zh: boolean) => {
  const item = leads[i];
  if (!item) return '';
  return zh
    ? `${item.short.zh}快 ${formatRatio(item.ratio)}`
    : `${formatRatio(item.ratio)} faster ${item.short.en}`;
};
/** The two biggest leads, joined: the claim of the headline */
const topLeads = (zh: boolean) =>
  leads.length >= 2
    ? `${leadPhrase(0, zh)}${zh ? '，' : ', '}${leadPhrase(1, zh)}`
    : '';
/** "saves 30×, bulk changes 15× and spawning 2.75×" */
const leadList = (zh: boolean, n = 3) => {
  const parts = leads
    .slice(0, n)
    .map((l) => `${l.short[zh ? 'zh' : 'en']} ${formatRatio(l.ratio)}`);
  if (zh) return parts.join('、');
  return parts.length > 1
    ? `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
    : parts.join('');
};
/** "146\u00a0μs": a time that does not break across lines */
const time = (ns: number) => formatTime(ns).replace(' ', '\u00a0');
/** The lead figure under the hero: the biggest lead; the cards under it show the next ones */
const topLead = leads[0];
const nextLeads = leads.slice(1, 4);
/** How many times EasyEcs' time the next-fastest library takes for the whole tick, when EasyEcs is the fastest */
const tickLead =
  peerList[0]?.ours && peerList[1]?.row.ratio
    ? peerList[1].row.ratio
    : undefined;
/**
 * The headline says "fastest" only while the data backs it: EasyEcs runs the
 * full server tick fastest, is faster beyond the measurement error in more
 * than half of the head-to-head comparisons, and slower beyond it in at most
 * one in ten. Otherwise it says "high-performance".
 */
const fastest =
  tickLead !== undefined &&
  counts.ahead * 2 > counts.total &&
  counts.slower * 10 <= counts.total;
/** "Friflo" -> "Friflo.Engine.ECS 3.6", with the version the comparison ran */
const versioned = (library: string) => peerLabel(library, docsVersion);
/** "Friflo.Engine.ECS 3.6, Arch 2.1 and DefaultEcs 0.17" */
const rivalNames = (zh: boolean) => {
  const names = peerList.filter((p) => !p.ours).map((p) => versioned(p.method));
  if (zh) return names.join('、');
  return names.length > 1
    ? `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
    : names.join('');
};

/** Where EasyEcs is behind, in a few words each */
const behindList = (zh: boolean) =>
  featureComparison('behind')
    .map((b) => b.short[zh ? 'zh' : 'en'])
    .join(zh ? '、' : ', ');

/**
 * The speed-ups behind the numbers, each as "how much faster than the alternative": a ratio of two rows of one
 * benchmark class, computed from the data so the copy never carries a number.
 */
const N100k = { Entities: '100000' };

function speedup(
  name: string,
  fast: string,
  slow: string,
  params: Record<string, string> = {},
) {
  const a = bench(name, fast, params);
  const b = bench(name, slow, params);
  return a && b && a.meanNs > 0 ? formatRatio(b.meanNs / a.meanNs) : '—';
}

const speed = {
  simd: speedup(
    'OperationBenchmark',
    'EasyEcs_Iterate2Simd',
    'EasyEcs_Iterate2',
    N100k,
  ),
  jobs: speedup('MicroBenchmarks', 'ForEachIterate', 'QueryIterate'),
  columns: speedup('MicroBenchmarks', 'ForEachSystemWrite', 'RawArrayWrite'),
  presence: speedup(
    'OperationBenchmark',
    'EasyEcs_Toggle',
    'Friflo_Toggle',
    N100k,
  ),
  ticks: speedup(
    'ChangeTrackingComparison',
    'EasyEcs_Changed',
    'EasyEcs_Scan',
    { Written: '10' },
  ),
  bulk: speedup(
    'BulkChangeComparison',
    'EasyEcs_SetAllRemoveAll',
    'Arch_AddRemoveByQuery',
  ),
  worlds:
    oneWorld && fourWorlds && fourWorlds.value > 0
      ? formatRatio(oneWorld.value / fourWorlds.value)
      : '—',
};

/* ------------------------------------------------------------------ */
/* Copy                                                                */
/* ------------------------------------------------------------------ */

interface Feature {
  icon: LucideIcon;
  title: string;
  text: string;
  href: string;
}

interface Faq {
  question: string;
  answer: string;
  /** A docs page that answers the question in depth, without the /{lang}/docs prefix */
  more?: { text: string; href: string };
}

interface Copy {
  description: string;
  pill: string;
  /** The h1 on two lines, the second in the gradient */
  headline: string;
  headlineAccent: string;
  /** The short paragraph under the h1 */
  lede: string;
  definition: string;
  start: string;
  github: string;
  demo: string;
  /** Over the lead figure: "Biggest lead, vs DefaultEcs" */
  statLabel: (library: string) => string;
  /** The lead figure's claim: what is measured, both times */
  statClaim: (lead: FeatureResult) => string;
  /** After a ratio: "faster" */
  faster: string;
  leadsTitle: string;
  tickTitle: string;
  tickText: string;
  allocTitle: string;
  whatTitle: string;
  facts: { term: string; detail: string; mono?: boolean }[];
  numbersTitle: string;
  numbers: { label: string; value: string; note: string }[];
  numbersNote: string;
  numbersLink: string;
  codeTitle: string;
  codeText: string;
  howTitle: string;
  howText: string;
  steps: { title: string; text: string; code: string }[];
  /** The link from the lead cards to the comparison page */
  compareLink: string;
  speedTitle: string;
  speedText: string;
  speedLink: string;
  speed: { title: string; text: string; metric: string; note: string }[];
  featuresTitle: string;
  featuresText: string;
  features: Feature[];
  serversTitle: string;
  serversText: string;
  serverPoints: string[];
  faqTitle: string;
  faq: Faq[];
  allFaq: string;
  ctaTitle: string;
  ctaText: string;
  ctaStart: string;
  ctaExample: string;
  ctaLlms: string;
}

const heroCode = `public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent, ITrackChanges;
public struct Dead : IComponent;                         // a tag
public record struct Clock(float Dt) : ISingletonComponent;

public class MovementSystem : EcsSystem
{
    [ForEach, Without<Dead>]                             // in reads, ref writes
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}

[UpdateAfter<MovementSystem>]
public class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>, Changed<Health>]            // only rows written since its last run
    static void Die(Entity unit, in Health hp)
    {
        if (hp.Value <= 0) unit.SetDead();               // immediate, no command buffer
    }
}`;

const stepCode = [
  `public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public struct Stunned : IComponent;            // a tag`,
  `public class MovementSystem : EcsSystem
{
    [ForEach, Without<Stunned>]
    static void Move(ref Position p, in Velocity v)
        => p = new(p.X + v.X, p.Y + v.Y);
}`,
  `using var world = new World();
world.AddSystem<MovementSystem>();
world.CreateEntity(new Position(0, 0), new Velocity(1, 0));
world.Update();                                // 0 B once warmed up`,
];

const copy: Record<Locale, Copy> = {
  en: {
    description: topLeads(false)
      ? `High-performance ECS for C# and .NET 10 servers: ${leadPhrase(0, false)} and ${leadPhrase(1, false)} than the next-fastest C# ECS, ${tickBytes} allocated per tick.`
      : `High-performance ECS for C# and .NET 10 servers: a full server tick in ${tickTime}, source-generated systems, change tracking and ${tickBytes} per tick.`,
    pill: `EasyEcs ${docsVersion} · C# ECS for .NET 10`,
    headline: fastest ? 'The fastest ECS' : 'High-performance ECS',
    headlineAccent: 'for .NET\u00a010 servers.',
    lede: `You write plain data and [ForEach] methods; a source generator turns them into inlined struct jobs. Faster than Friflo, Arch and DefaultEcs in ${counts.ahead} of ${counts.total} head-to-head benchmarks${zeroAlloc ? ', and a server tick allocates nothing' : `, ${tickBytes} allocated per tick`}.`,
    definition: `EasyEcs is an open-source, high-performance archetype Entity Component System (ECS) for C# and .NET 10 servers. In ${counts.total} head-to-head benchmarks against Friflo.Engine.ECS, Arch and DefaultEcs, each library doing the same work its own way, it is faster in ${counts.ahead} and within the measurement error in ${counts.even}${zeroAlloc ? ', and a full server tick allocates nothing' : ''}. Write plain data and [ForEach] methods; a source generator turns them into inlined struct jobs, and an analyzer catches design mistakes at compile time.`,
    start: 'Get started',
    github: 'View on GitHub',
    demo: 'Try it in the browser',
    statLabel: (library) => `Biggest lead, vs ${library}`,
    statClaim: (l) =>
      `${l.label.en}: ${time(l.ours.meanNs)} with EasyEcs, ${time(l.versus.row.meanNs)} with ${libraryName(l.versus.library)}.`,
    faster: 'faster',
    leadsTitle: 'The next biggest leads',
    tickTitle: 'One server tick, 100,000 entities',
    tickText:
      'Nine systems; entities spawn, die, gain and lose components every tick.',
    allocTitle: 'Allocated per tick',
    whatTitle: 'What is EasyEcs?',
    facts: [
      { term: 'Install', detail: installCommand, mono: true },
      {
        term: 'Runs on',
        detail:
          '.NET 10 (net10.0): game servers, simulations and other long-running backends. Not Unity.',
      },
      {
        term: 'Compared with',
        detail: `${rivalNames(false)}: faster in ${counts.ahead} of ${counts.total} head-to-head benchmarks, within the measurement error in ${counts.even}`,
      },
      {
        term: 'Ships with',
        detail:
          'The source generator, 23 analyzer rules and their code fixes, in one NuGet package',
      },
      { term: 'License', detail: 'MIT, open source on GitHub' },
    ],
    numbersTitle: 'One server tick, measured',
    numbers: [
      {
        label: 'Four worlds, one per thread',
        value: text(fourWorlds),
        note: '4 × 25,000 entities',
      },
      {
        label: 'Battle server, every feature',
        value: text(battle),
        note: '100,000 units, parallel, replication, indexes',
      },
      {
        label: '[ForEach] write',
        value: text(perEntity),
        note:
          perEntity && rawArray && perEntity.value < rawArray.value
            ? 'per entity, faster than two plain arrays'
            : 'per entity, one archetype',
      },
      {
        label: 'Allocated per tick',
        value: text(tickAlloc),
        note: 'one world of 100,000 entities, after warm-up',
      },
    ],
    numbersNote: `Entities spawn, die, gain and lose components every tick. ${sourceLine(false)}`,
    numbersLink: 'What the tick simulates',
    codeTitle: 'Plain data in, inlined jobs out',
    codeText:
      'Components are structs; a system is a method. Filters are attributes, singletons are parameters, and the generator writes the loop.',
    howTitle: 'You write the logic. The generator writes the plumbing.',
    howText:
      'Systems cooperate only through the data they write, so each one does one job and can be read, tested and reordered on its own.',
    steps: [
      {
        title: 'Describe the data',
        text: 'Components are plain structs. Tags are empty structs; singletons hold per-world state. No base classes, no registration.',
        code: stepCode[0],
      },
      {
        title: 'Write one method per system',
        text: 'A [ForEach] method is the whole system. The generator turns it into an inlined struct job; filters are attributes.',
        code: stepCode[1],
      },
      {
        title: 'Tick the world',
        text: 'The host owns the world. Systems run in registration order unless [UpdateBefore] or [UpdateAfter] say otherwise.',
        code: stepCode[2],
      },
    ],
    compareLink: 'Full comparison',
    featuresTitle: 'Everything a long-running simulation needs',
    featuresText:
      'Each feature is opt-in and costs nothing until a world uses it.',
    features: [
      {
        icon: Zap,
        title: 'Zero garbage per tick',
        text: 'After warm-up, updates, queries and structural changes allocate nothing. A unit test enforces the 0 B budget on every run.',
        href: 'performance',
      },
      {
        icon: Sparkles,
        title: 'Source-generated systems',
        text: 'Write a [ForEach] method; get a JIT-inlined struct job. Singletons are parameters, filters are attributes, entity members are generated.',
        href: 'guide/systems',
      },
      {
        icon: Layers,
        title: 'Immediate structural changes',
        text: 'Add, remove, spawn and destroy in the middle of a pass, with no command buffer to flush. Toggling a status flips a presence bit; the entity stays put.',
        href: 'guide/structural-changes',
      },
      {
        icon: Radar,
        title: 'Change tracking built in',
        text: '[Changed<T>] visits only the rows written since a system last ran, writes through ref included. [Removed<T>] finds the entities that lost a component.',
        href: 'guide/change-tracking',
      },
      {
        icon: Network,
        title: 'Indexes, relations, hierarchy',
        text: 'Find entities by value, follow links both ways, walk children and cascade destroys. Every Set keeps them current.',
        href: 'guide/indexes-and-relations',
      },
      {
        icon: Cpu,
        title: 'Parallel without data races',
        text: 'Systems whose read and write sets do not conflict run side by side; parallel [ForEach] splits rows across cores. The analyzer rejects methods that would race.',
        href: 'guide/threading',
      },
      {
        icon: Stamp,
        title: 'Prefabs outside the world',
        text: 'A prefab is a recipe that belongs to no world, so no query has to filter templates out. Instantiate spawns a whole wave in one pass.',
        href: 'guide/prefabs',
      },
      {
        icon: History,
        title: 'Snapshots, saves, migration',
        text: 'Capture and restore a world in memory for undo, prediction and replays. Save a whole world, or move a group of entities to another world or process.',
        href: 'guide/snapshots',
      },
      {
        icon: ShieldCheck,
        title: 'Mistakes fail the build',
        text: '23 analyzer rules with code fixes catch stateful systems, systems that call each other, writes through in, and structural changes off the owner thread.',
        href: 'guide/analyzer',
      },
    ],
    speedTitle: 'Why it is fast',
    speedText:
      'No single trick: every layer, from the memory layout to the generated loop, is built so the CPU does only the work the simulation needs. Each claim below is a measured ratio.',
    speedLink: 'How each one works',
    speed: [
      {
        title: 'SIMD over whole columns',
        text: 'A batch [ForEach] hands you spans of a column, so Vector<T> math runs over contiguous rows.',
        metric: speed.simd,
        note: 'two-component update, SIMD batch vs per entity',
      },
      {
        title: 'Generated struct jobs',
        text: 'The generator turns a [ForEach] method into a struct the JIT inlines: no delegates, no enumerator, no virtual calls per entity.',
        metric: speed.jobs,
        note: '[ForEach] job vs a foreach over Query',
      },
      {
        title: '64-byte aligned columns',
        text: 'Each component is a column aligned to the cache line, walked front to back, with bounds checks proven away.',
        metric: speed.columns,
        note: 'faster than two plain arrays',
      },
      {
        title: 'Presence bits',
        text: 'Adding a status back that an entity lost flips a bit in its row; the entity never moves between tables.',
        metric: speed.presence,
        note: 'toggling a status vs Friflo',
      },
      {
        title: 'Change-tick summaries',
        text: 'Write ticks are summarized per 64 rows and per 4,096 rows, so [Changed<T>] skips everything nobody wrote.',
        metric: speed.ticks,
        note: '10 of 100,000 written, vs a scan',
      },
      {
        title: 'Whole words of rows',
        text: 'SetAll and RemoveAll change 64 rows per step through presence bits instead of moving each entity.',
        metric: speed.bulk,
        note: 'stun 100,000 and lift it, vs Arch',
      },
      {
        title: 'One world per core',
        text: 'Worlds share nothing, so one world per thread scales without locks; parallel systems and [ForEach] use the cores of one world.',
        metric: speed.worlds,
        note: 'four worlds vs one, same 100,000 entities',
      },
      {
        title: 'Zero allocation',
        text: 'Pooled, pinned, recycled: after warm-up a tick allocates nothing, and a unit test fails the build if it ever does.',
        metric: zeroAlloc ? '0 B' : text(tickAlloc),
        note: 'per tick, enforced in CI',
      },
    ],
    serversTitle: 'Made for processes that run for months',
    serversText:
      'One owner thread per world and one world per thread: game servers, simulations and other real-time backends scale across cores without locks in your code.',
    serverPoints: [
      'Entity ids are recycled behind version-checked handles, so a stale handle never reaches the wrong entity.',
      'Memory is bounded by the peak entity count; tables can be compacted while the server runs.',
      'A structural change from the wrong thread throws, or is reported at compile time, instead of corrupting the tables.',
      'Plugin types unload with their collectible AssemblyLoadContext, so game logic can be hot-reloaded.',
    ],
    faqTitle: 'Questions about EasyEcs and ECS in C#',
    faq: [
      {
        question: 'What is an Entity Component System (ECS)?',
        answer:
          'An Entity Component System is a way to structure a program around data. Entities are ids, components are plain data attached to entities, and systems are the logic that runs over every entity with a given set of components. Systems cooperate only through the data they write, so each does one job and can be tested and reordered on its own. EasyEcs is an archetype ECS: entities with the same set of components share a table whose columns are walked front to back.',
        more: { text: 'What is an ECS?', href: 'what-is-ecs' },
      },
      {
        question: 'What is the fastest ECS for C# and .NET?',
        answer: `It depends on the work. The EasyEcs repository measures it against Friflo.Engine.ECS, Arch and DefaultEcs in ${counts.total} head-to-head benchmarks with BenchmarkDotNet on .NET 10, each library doing the same work its own idiomatic way, with identical results checked in setup. EasyEcs is faster in ${counts.ahead}, within the measurement error in ${counts.even} and slower in ${counts.slower}. Its biggest leads over the next-fastest library: ${leadList(false, 5)}. On the full 100,000-entity server tick: ${peerTimes(false)}.${slowRange ? ` The two fastest allocate nothing per tick; the others take ${slowRange} as long and allocate every tick.` : ''}${behindList(false) ? ` Another library comes out ahead on ${behindList(false)}.` : ''}`,
        more: {
          text: 'Comparison with Friflo, Arch and DefaultEcs',
          href: 'comparison',
        },
      },
      {
        question:
          'How is EasyEcs different from Arch, Friflo.Engine.ECS and DefaultEcs?',
        answer:
          'Structural changes apply immediately, even in the middle of a query, so there is no command buffer to flush. Systems are generated from [ForEach] methods and checked at compile time by 23 analyzer rules. Change tracking, removal detection, prefabs, value indexes, relations and in-memory snapshots are built in. The other three run on Unity and older runtimes; Friflo also offers events and JSON serialization, and DefaultEcs reactive sets and serializers.',
        more: { text: 'Which library to pick', href: 'comparison' },
      },
      {
        question: 'Does EasyEcs allocate memory during updates?',
        answer: `No. After warm-up, updates, queries and structural changes allocate 0 bytes, and a unit test enforces that budget on every run.${zeroAlloc ? ' A full server tick of 100,000 entities allocates 0 B.' : ''}`,
        more: { text: 'Performance guide', href: 'performance' },
      },
      {
        question: 'Does EasyEcs work with Unity?',
        answer:
          'No. EasyEcs targets net10.0 only and is built for servers: game servers, simulations and other long-running .NET backends. For Unity or older runtimes, Friflo.Engine.ECS, Arch and DefaultEcs are the alternatives.',
      },
      {
        question: 'How do I install EasyEcs?',
        answer: `Run "${installCommand}" in a .NET 10 project. The NuGet package includes the source generator, the analyzer and its code fixes; nothing else needs to be registered.`,
        more: { text: 'Getting started', href: 'getting-started' },
      },
      {
        question: 'Is EasyEcs free and open source?',
        answer:
          'Yes. EasyEcs is open source under the MIT license, and its source, tests and benchmarks are on GitHub.',
      },
    ],
    allFaq: 'All design questions',
    ctaTitle: 'Build your first world in ten minutes',
    ctaText:
      'The tutorial walks through entities, components, queries, systems, singletons and the update loop, one runnable step at a time.',
    ctaStart: 'Read the tutorial',
    ctaExample: 'See a full battle server',
    ctaLlms: 'Docs for AI assistants',
  },
  zh: {
    description: topLeads(true)
      ? `高性能 C# / .NET 10 服务端 ECS 框架：${leads[0].short.zh}比次快的 C# ECS 快 ${formatRatio(leads[0].ratio)}，${leadPhrase(1, true)}，每个 tick 分配 ${tickBytes}。`
      : `高性能 C# / .NET 10 服务端 ECS 框架：完整服务器 tick 只要 ${tickTime}，源生成系统、变更追踪，每个 tick 分配 ${tickBytes}。`,
    pill: `EasyEcs ${docsVersion} · C# 高性能 ECS 框架`,
    headline: fastest ? '最快的 ECS 框架' : '高性能 ECS 框架',
    headlineAccent: '为 .NET\u00a010 服务端而生。',
    lede: `你只写普通数据和 [ForEach] 方法，源生成器把它们编译成内联的结构体 job。与 Friflo、Arch、DefaultEcs 正面对比 ${counts.total} 项，${counts.ahead} 项更快${zeroAlloc ? '，服务器 tick 不分配任何内存' : `，每个 tick 分配 ${tickBytes}`}。`,
    definition: `EasyEcs 是开源、高性能的 archetype（原型）实体组件系统（ECS），面向 C# 与 .NET 10 服务端。与 Friflo.Engine.ECS、Arch、DefaultEcs 的 ${counts.total} 项正面对比里，每个库用自己的写法做同样的事，它在 ${counts.ahead} 项更快，${counts.even} 项差距在测量误差之内${zeroAlloc ? '，完整的服务器 tick 也不分配任何内存' : ''}。你只写普通数据和 [ForEach] 方法：源生成器把它们编译成内联的结构体 job，分析器在编译期拦下设计错误。`,
    start: '快速开始',
    github: '在 GitHub 上查看',
    demo: '在浏览器里试试',
    statLabel: (library) => `领先最多的一项，对比 ${library}`,
    statClaim: (l) =>
      `${l.label.zh}：EasyEcs 用时 ${time(l.ours.meanNs)}，${libraryName(l.versus.library)} 要 ${time(l.versus.row.meanNs)}。`,
    faster: '更快',
    leadsTitle: '领先幅度紧随其后的几项',
    tickTitle: '一个服务器 tick，10 万实体',
    tickText: '9 个系统；每个 tick 都有实体生成、死亡、增减组件。',
    allocTitle: '每个 tick 的分配',
    whatTitle: 'EasyEcs 是什么？',
    facts: [
      { term: '安装', detail: installCommand, mono: true },
      {
        term: '运行环境',
        detail:
          '.NET 10（net10.0）：游戏服务器、模拟以及其他长时间运行的后端。不支持 Unity。',
      },
      {
        term: '对比',
        detail: `${rivalNames(true)}：${counts.total} 项正面对比中 ${counts.ahead} 项更快，${counts.even} 项差距在测量误差之内`,
      },
      {
        term: '包含',
        detail: '源生成器、23 条分析器规则及代码修复，都在同一个 NuGet 包里',
      },
      { term: '协议', detail: 'MIT，在 GitHub 上开源' },
    ],
    numbersTitle: '一个服务器 tick 的实测耗时',
    numbers: [
      {
        label: '4 个 World，每线程一个',
        value: text(fourWorlds),
        note: '4 × 2.5 万实体',
      },
      {
        label: '战斗服示例，全部功能',
        value: text(battle),
        note: '10 万单位，含并行、同步、索引',
      },
      {
        label: '[ForEach] 写入',
        value: text(perEntity),
        note:
          perEntity && rawArray && perEntity.value < rawArray.value
            ? '每个实体，比两个普通数组还快'
            : '每个实体，单个原型',
      },
      {
        label: '每个 tick 的分配',
        value: text(tickAlloc),
        note: '单个 World，10 万实体，预热之后',
      },
    ],
    numbersNote: `每个 tick 都有实体生成、死亡、增减组件。${sourceLine(true)}`,
    numbersLink: '这个 tick 模拟了什么',
    codeTitle: '写的是数据，跑的是内联 job',
    codeText:
      '组件是结构体，系统是一个方法。过滤条件写成特性，单例写成参数，循环由生成器来写。',
    howTitle: '你写业务逻辑，样板代码交给生成器',
    howText:
      '系统之间只通过数据协作，每个系统只做一件事，可以单独阅读、测试和调整顺序。',
    steps: [
      {
        title: '定义数据',
        text: '组件是普通结构体，空结构体就是标签，单例存放 World 级别的状态。不用继承基类，也不用注册。',
        code: stepCode[0],
      },
      {
        title: '一个系统一个方法',
        text: '一个 [ForEach] 方法就是整个系统。生成器把它编译成内联的结构体 job，过滤条件写成特性即可。',
        code: stepCode[1],
      },
      {
        title: '驱动 World',
        text: 'World 由宿主持有。系统按注册顺序运行，除非用 [UpdateBefore] 或 [UpdateAfter] 指定先后。',
        code: stepCode[2],
      },
    ],
    compareLink: '完整对比',
    featuresTitle: '长时间运行的模拟需要的，都在这里',
    featuresText: '每个功能都按需启用，World 不用就没有开销。',
    features: [
      {
        icon: Zap,
        title: '每个 tick 零垃圾',
        text: '预热之后，更新、查询和结构变更都不分配内存。单元测试在每次运行时守住 0 B 的预算。',
        href: 'performance',
      },
      {
        icon: Sparkles,
        title: '源生成的系统',
        text: '写一个 [ForEach] 方法，得到可被 JIT 内联的结构体 job。单例是参数，过滤是特性，实体成员由生成器生成。',
        href: 'guide/systems',
      },
      {
        icon: Layers,
        title: '结构变更立即生效',
        text: '遍历途中也能增删组件、创建销毁实体，没有命令缓冲要提交。切换状态只翻转一个存在位，实体原地不动。',
        href: 'guide/structural-changes',
      },
      {
        icon: Radar,
        title: '内置变更追踪',
        text: '[Changed<T>] 只访问系统上次运行后被写过的行，经 ref 的写入也算。[Removed<T>] 找出失去某个组件的实体。',
        href: 'guide/change-tracking',
      },
      {
        icon: Network,
        title: '索引、关系与层级',
        text: '按值查找实体，双向跟踪链接，遍历子实体并级联销毁。每次 Set 都会自动维护。',
        href: 'guide/indexes-and-relations',
      },
      {
        icon: Cpu,
        title: '并行且无数据竞争',
        text: '读写集合不冲突的系统并行执行；并行 [ForEach] 把行拆到多个核心。可能产生竞争的方法会被分析器拒绝。',
        href: 'guide/threading',
      },
      {
        icon: Stamp,
        title: 'World 之外的预制体',
        text: '预制体是一份不属于任何 World 的配方，查询不必过滤模板。Instantiate 一次就能生成一整波实体。',
        href: 'guide/prefabs',
      },
      {
        icon: History,
        title: '快照、存档与迁移',
        text: '在内存里捕获和恢复 World，用于撤销、预测和回放。保存整个 World，或把一组实体迁到另一个 World 或进程。',
        href: 'guide/snapshots',
      },
      {
        icon: ShieldCheck,
        title: '写错了就编译不过',
        text: '23 条带代码修复的分析器规则，拦下有状态的系统、系统互相调用、经 in 写入、在非所有者线程做结构变更等问题。',
        href: 'guide/analyzer',
      },
    ],
    speedTitle: '为什么这么快',
    speedText:
      '没有单一的秘诀：从内存布局到生成的循环，每一层都只让 CPU 做模拟真正需要的事。下面每一条都是实测的倍数。',
    speedLink: '逐项看原理',
    speed: [
      {
        title: '整列 SIMD',
        text: '批量 [ForEach] 直接拿到一列组件的 Span，Vector<T> 运算在连续的行上跑。',
        metric: speed.simd,
        note: '双组件更新，SIMD 批量对比逐实体',
      },
      {
        title: '生成的 struct job',
        text: '生成器把 [ForEach] 方法变成 JIT 能内联的结构体：每个实体不经过委托、枚举器或虚调用。',
        metric: speed.jobs,
        note: '[ForEach] job 对比 foreach 遍历 Query',
      },
      {
        title: '64 字节对齐的列',
        text: '每个组件一列，按缓存行对齐，从头到尾顺序扫描，边界检查在证明安全后省掉。',
        metric: speed.columns,
        note: '比两个普通数组还快',
      },
      {
        title: 'presence bit',
        text: '实体失去又重新获得某个状态时，只翻转所在行的一个位，实体不会在表之间搬家。',
        metric: speed.presence,
        note: '切换状态，对比 Friflo',
      },
      {
        title: '变更 tick 摘要',
        text: '写入 tick 按每 64 行、每 4,096 行汇总，[Changed<T>] 直接跳过没人写过的部分。',
        metric: speed.ticks,
        note: '10 万中写了 10 个，对比全表扫描',
      },
      {
        title: '按字批量处理行',
        text: 'SetAll、RemoveAll 通过 presence bit 一步处理 64 行，而不是逐个搬动实体。',
        metric: speed.bulk,
        note: '给 10 万个单位上眩晕再解除，对比 Arch',
      },
      {
        title: '一核一个 World',
        text: 'World 之间不共享任何东西，一个线程一个 World 就能无锁扩展；单个 World 内也有并行系统和并行 [ForEach]。',
        metric: speed.worlds,
        note: '同样 10 万实体，4 个 World 对比 1 个',
      },
      {
        title: '零分配',
        text: '池化、固定、复用：预热之后每个 tick 不分配任何内存，一旦分配，单元测试就会让构建失败。',
        metric: zeroAlloc ? '0 B' : text(tickAlloc),
        note: '每个 tick，CI 强制检查',
      },
    ],
    serversTitle: '为一跑就是几个月的进程而生',
    serversText:
      '每个 World 一个所有者线程，每个线程一个 World：游戏服务器、模拟和其他实时后端可以铺满多核，业务代码里不用加锁。',
    serverPoints: [
      '实体 id 会回收，句柄带版本校验，过期的句柄不会误伤复用了 id 的新实体。',
      '内存上限由实体数量峰值决定；服务器运行中也可以压缩表。',
      '在错误的线程上做结构变更会直接抛异常，或者在编译期就被报出来，不会悄悄破坏表。',
      '插件类型随可回收的 AssemblyLoadContext 一起卸载，游戏逻辑可以热重载。',
    ],
    faqTitle: '关于 EasyEcs 与 C# ECS 的常见问题',
    faq: [
      {
        question: '什么是实体组件系统（ECS）？',
        answer:
          '实体组件系统是一种围绕数据组织程序的方式。实体就是一个 id，组件是挂在实体上的普通数据，系统是逻辑，对拥有某组组件的所有实体统一执行。系统之间只通过写入的数据协作，所以每个系统只做一件事，可以单独测试、调整顺序。EasyEcs 是 archetype（原型）ECS：组件组合相同的实体共用一张表，按列从头到尾遍历。',
        more: { text: '什么是 ECS？', href: 'what-is-ecs' },
      },
      {
        question: 'C# / .NET 里最快的 ECS 是哪个？',
        answer: `取决于负载。EasyEcs 仓库在 .NET 10 上用 BenchmarkDotNet 把它和 Friflo.Engine.ECS、Arch、DefaultEcs 做了 ${counts.total} 项正面对比，每个库都用自己惯用的写法做同样的事，并在 setup 中校验结果一致。EasyEcs 在 ${counts.ahead} 项更快，${counts.even} 项差距在测量误差之内，${counts.slower} 项更慢。领先次快的库最多的几项：${leadList(true, 5)}。10 万实体的完整服务器 tick：${peerTimes(true)}。${slowRange ? `最快的两个库每个 tick 都不分配内存；另外两个耗时是它的 ${slowRange}，而且每个 tick 都有分配。` : ''}${behindList(true) ? `其他库领先的项目：${behindList(true)}。` : ''}`,
        more: {
          text: '与 Friflo、Arch、DefaultEcs 的对比',
          href: 'comparison',
        },
      },
      {
        question: 'EasyEcs 和 Arch、Friflo.Engine.ECS、DefaultEcs 有什么不同？',
        answer:
          '结构变更立即生效，查询途中也一样，没有命令缓冲需要提交。系统由 [ForEach] 方法生成，并在编译期接受 23 条分析器规则的检查。变更追踪、移除检测、预制体、值索引、关系和内存快照都是内置的。另外三个库支持 Unity 和更老的运行时；Friflo 还提供事件和 JSON 序列化，DefaultEcs 提供响应式集合和序列化器。',
        more: { text: '该选哪个库', href: 'comparison' },
      },
      {
        question: 'EasyEcs 在更新时会分配内存吗？',
        answer: `不会。预热之后，更新、查询和结构变更都分配 0 字节，单元测试在每次运行时都会检查这个预算。${zeroAlloc ? '10 万实体的完整服务器 tick 分配 0 B。' : ''}`,
        more: { text: '性能指南', href: 'performance' },
      },
      {
        question: 'EasyEcs 能在 Unity 里用吗？',
        answer:
          '不能。EasyEcs 只面向 net10.0，专为服务端打造：游戏服务器、模拟以及其他长时间运行的 .NET 后端。Unity 或更老的运行时可以选择 Friflo.Engine.ECS、Arch 或 DefaultEcs。',
      },
      {
        question: '怎么安装 EasyEcs？',
        answer: `在 .NET 10 项目里运行 "${installCommand}"。NuGet 包里已经带了源生成器、分析器和代码修复，不需要再注册任何东西。`,
        more: { text: '快速开始', href: 'getting-started' },
      },
      {
        question: 'EasyEcs 免费开源吗？',
        answer:
          '是的。EasyEcs 采用 MIT 协议开源，源码、测试和基准测试都在 GitHub 上。',
      },
    ],
    allFaq: '全部设计问答',
    ctaTitle: '十分钟搭出你的第一个 World',
    ctaText:
      '教程一步一步带你走完实体、组件、查询、系统、单例和更新循环，每一步都能直接运行。',
    ctaStart: '阅读教程',
    ctaExample: '看一个完整的战斗服',
    ctaLlms: '给 AI 助手的文档',
  },
};

// Search result titles: the product and the category people search for.
const seoTitle: Record<Locale, string> = {
  en: 'EasyEcs – High-Performance ECS for C# and .NET 10 Servers',
  zh: 'EasyEcs – 高性能 C# / .NET 10 服务端 ECS 框架',
};

/** The home FAQ as plain text, for the FAQPage JSON-LD */
function homeFaq(lang: Locale) {
  return copy[lang].faq.map(({ question, answer }) => ({ question, answer }));
}

/* ------------------------------------------------------------------ */
/* Route                                                               */
/* ------------------------------------------------------------------ */

// Code is highlighted at build time (the page is prerendered), with both
// themes in the HTML: app.css switches them with the dark class.
export async function loader() {
  const highlighter = await getHighlighter('js', {
    langs: ['csharp'],
    themes: ['github-light', 'github-dark'],
  });
  const html = (code: string) =>
    highlighter.codeToHtml(code, {
      lang: 'csharp',
      themes: { light: 'github-light', dark: 'github-dark' },
      defaultColor: false,
    });
  return { hero: html(heroCode), steps: stepCode.map(html) };
}

export function meta({ params }: Route.MetaArgs) {
  const lang = getLocale(params.lang);
  return homeMeta({
    lang,
    title: seoTitle[lang],
    description: copy[lang].description,
    faq: homeFaq(lang),
  });
}

function Code({ html, className }: { html: string; className?: string }) {
  return (
    <div
      className={cn(
        'home-code overflow-x-auto font-mono text-[13px] leading-relaxed [&_pre]:p-5',
        className,
      )}
      // biome-ignore lint/security/noDangerouslySetInnerHtml: Shiki's HTML for the page's own code samples, made at build time
      dangerouslySetInnerHTML={{ __html: html }}
    />
  );
}

/** A text link with an arrow: "The full comparison →" */
function MoreLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link
      to={to}
      className="group inline-flex items-center gap-1 text-sm font-medium text-fd-foreground underline-offset-4 hover:underline"
    >
      {children}
      <ArrowRight
        className="size-3.5 transition-transform group-hover:translate-x-0.5"
        aria-hidden="true"
      />
    </Link>
  );
}

/** GitHub's mark, for the outlined GitHub button (lucide has no brand icons) */
function GitHubIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z" />
    </svg>
  );
}

interface Bar {
  name: string;
  ns: number;
  ours?: boolean;
}

/**
 * Horizontal bars on a linear scale, the longest time full width: EasyEcs in
 * the accent, the others in grey. The name and the time are text, so a
 * screen reader reads "EasyEcs 146 μs, DefaultEcs 4.33 ms"; the bars are
 * decoration.
 */
function Bars({ rows }: { rows: Bar[] }) {
  const max = Math.max(...rows.map((r) => r.ns));
  return (
    <ul className="flex flex-col gap-1.5">
      {rows.map((r) => (
        <li
          key={r.name}
          className="grid grid-cols-[5.5rem_minmax(0,1fr)_4.5rem] items-center gap-3 text-xs"
        >
          <span
            className={cn(
              'truncate',
              r.ours ? 'font-medium' : 'text-fd-muted-foreground',
            )}
          >
            {r.name}
          </span>
          <span className="relative h-1.5" aria-hidden="true">
            <span
              className="absolute inset-y-0 left-0 min-w-1 rounded-full"
              style={{
                width: `${(r.ns / max) * 100}%`,
                background: r.ours
                  ? 'var(--ecs-chart-ours)'
                  : 'var(--ecs-chart-other)',
              }}
            />
          </span>
          <span
            className={cn(
              'text-right tabular-nums',
              r.ours ? 'font-medium' : 'text-fd-muted-foreground',
            )}
          >
            {formatTime(r.ns)}
          </span>
        </li>
      ))}
    </ul>
  );
}

/** EasyEcs against the library a comparison is measured against */
const leadBars = (l: FeatureResult): Bar[] => [
  { name: 'EasyEcs', ns: l.ours.meanNs, ours: true },
  { name: l.versus.library, ns: l.versus.row.meanNs },
];

/** One lead in a card: what is measured, the ratio, the two bars */
function LeadRow({
  lead,
  lang,
  faster,
}: {
  lead: FeatureResult;
  lang: Locale;
  faster: string;
}) {
  return (
    <div className="flex flex-col gap-3 px-5 py-5 sm:px-6">
      <div className="flex items-start justify-between gap-4">
        <h3 className="text-sm font-medium">{lead.label[lang]}</h3>
        <p className="flex shrink-0 items-baseline gap-1.5 leading-none">
          <span className="text-2xl font-semibold tracking-tight tabular-nums">
            {formatRatio(lead.ratio)}
          </span>
          <span className="text-xs text-fd-muted-foreground">{faster}</span>
        </p>
      </div>
      <Bars rows={leadBars(lead)} />
    </div>
  );
}

/** A card of the lead section: a title, rows split by hairlines, a link at the foot */
function StatCard({
  title,
  link,
  children,
}: {
  title: string;
  link: { to: string; text: string };
  children: ReactNode;
}) {
  return (
    <section className={cn(surface, 'flex flex-col')}>
      <h2 className="px-5 pt-5 pb-1 font-semibold tracking-tight sm:px-6">
        {title}
      </h2>
      <div className="flex-1 divide-y">{children}</div>
      <p className="border-t px-5 py-4 sm:px-6">
        <MoreLink to={link.to}>{link.text}</MoreLink>
      </p>
    </section>
  );
}

function Section({
  id,
  title,
  text,
  children,
}: {
  id?: string;
  title: string;
  text?: string;
  children: ReactNode;
}) {
  return (
    <section id={id} className="w-full border-t px-4 py-16 sm:px-6 md:py-24">
      <div className="mx-auto max-w-6xl">
        <h2 className="max-w-3xl text-2xl font-bold tracking-tight text-balance sm:text-3xl">
          {title}
        </h2>
        {text && (
          <p className="mt-3 max-w-2xl leading-relaxed text-fd-muted-foreground">
            {text}
          </p>
        )}
        <div className="mt-10">{children}</div>
      </div>
    </section>
  );
}

export default function HomePage({ params, loaderData }: Route.ComponentProps) {
  const lang = getLocale(params.lang);
  const t = copy[lang];
  const docs = `/${lang}/docs`;

  return (
    <HomeLayout {...baseOptions(lang)}>
      <main className="flex flex-1 flex-col items-center">
        {/* Hero, as on the family sites: centred claim, the second line in the gradient */}
        <section className="relative isolate w-full px-4 pt-12 pb-12 sm:px-6 md:pt-20 md:pb-16">
          <div
            aria-hidden="true"
            className="ecs-hero-glow pointer-events-none absolute inset-0 -z-10"
          />
          <div className="mx-auto flex max-w-5xl flex-col items-center text-center">
            <span className="mb-4 rounded-full border bg-fd-secondary/50 px-3 py-1 text-xs font-medium text-fd-muted-foreground">
              {t.pill}
            </span>
            <h1 className="text-4xl font-extrabold tracking-tight text-balance break-keep sm:text-5xl lg:text-6xl">
              {t.headline}{' '}
              <span className="ecs-gradient-text mx-auto block w-fit pb-1">
                {t.headlineAccent}
              </span>
            </h1>
            <p className="mt-3 max-w-xl text-base leading-relaxed text-balance text-fd-muted-foreground md:text-lg">
              {t.lede}
            </p>
            <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
              <Link to={`${docs}/getting-started`} className={button()}>
                {t.start}
                <ArrowRight />
              </Link>
              <a
                href={githubUrl}
                target="_blank"
                rel="noopener noreferrer"
                className={button('secondary')}
              >
                <GitHubIcon />
                {t.github}
              </a>
            </div>
            {/* Fumadocs' code block: the same copy button as every code sample in the docs */}
            <CodeBlock
              className="my-0 mt-4 w-full max-w-sm text-start"
              keepBackground={false}
            >
              <Pre className="px-4">
                <code>{installCommand}</code>
              </Pre>
            </CodeBlock>
            <p className="mt-4">
              <MoreLink to={`${docs}/demo`}>{t.demo}</MoreLink>
            </p>
          </div>
        </section>

        {/* The biggest lead as one figure, then the next leads and the whole tick */}
        {topLead && (
          <section className="w-full border-t px-4 py-12 sm:px-6 md:py-16">
            <div className="mx-auto flex max-w-5xl flex-col gap-10">
              <div className="grid items-end gap-x-14 gap-y-5 lg:grid-cols-[auto_minmax(0,1fr)]">
                <div>
                  <p className="text-sm text-fd-muted-foreground">
                    {t.statLabel(libraryName(topLead.versus.library))}
                  </p>
                  <p className="ecs-gradient-text mt-2 w-fit pb-2 text-[5.5rem] leading-none font-extrabold tracking-tighter tabular-nums sm:text-9xl">
                    {formatRatio(topLead.ratio)}
                  </p>
                </div>
                <div className="flex flex-col gap-4 lg:pb-4">
                  <p className="max-w-md text-base leading-snug text-pretty sm:text-lg">
                    {t.statClaim(topLead)}
                  </p>
                  <Bars rows={leadBars(topLead)} />
                </div>
              </div>
              <div className="grid gap-6 lg:grid-cols-2 lg:gap-x-8">
                <StatCard
                  title={t.leadsTitle}
                  link={{
                    to: `${docs}/comparison#feature-by-feature`,
                    text: t.compareLink,
                  }}
                >
                  {nextLeads.map((l) => (
                    <LeadRow
                      key={l.label.en}
                      lead={l}
                      lang={lang}
                      faster={t.faster}
                    />
                  ))}
                </StatCard>
                <StatCard
                  title={t.tickTitle}
                  link={{
                    to: `${docs}/performance#benchmarks`,
                    text: t.numbersLink,
                  }}
                >
                  <div className="flex flex-col gap-3 px-5 py-5 sm:px-6">
                    <div className="flex items-start justify-between gap-4">
                      <h3 className="text-sm text-fd-muted-foreground">
                        {t.tickText}
                      </h3>
                      {tickLead && (
                        <p className="flex shrink-0 items-baseline gap-1.5 leading-none">
                          <span className="text-2xl font-semibold tracking-tight tabular-nums">
                            {formatRatio(tickLead)}
                          </span>
                          <span className="text-xs text-fd-muted-foreground">
                            {t.faster}
                          </span>
                        </p>
                      )}
                    </div>
                    <Bars
                      rows={peerList.map((p) => ({
                        name: p.method,
                        ns: p.row.meanNs,
                        ours: p.ours,
                      }))}
                    />
                  </div>
                  <div className="px-5 py-5 sm:px-6">
                    <h3 className="text-sm font-medium">{t.allocTitle}</h3>
                    <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-4">
                      {peerList.map((p) => (
                        <div key={p.method}>
                          <dt className="text-xs text-fd-muted-foreground">
                            {p.method}
                          </dt>
                          <dd
                            className={cn(
                              'mt-0.5 text-lg tracking-tight tabular-nums',
                              p.ours && 'font-semibold',
                            )}
                          >
                            {formatBytes(p.row.allocatedBytes)}
                          </dd>
                        </div>
                      ))}
                    </dl>
                  </div>
                </StatCard>
              </div>
              <p className="text-xs leading-relaxed text-fd-muted-foreground">
                {sourceLine(lang === 'zh')}
              </p>
            </div>
          </section>
        )}

        {/* What EasyEcs is, in a paragraph and the facts beside it */}
        <section className="w-full border-t px-4 py-16 sm:px-6 md:py-20">
          <div className="mx-auto grid max-w-5xl gap-10 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] lg:gap-14">
            <div>
              <h2 className="text-2xl font-bold tracking-tight sm:text-3xl">
                {t.whatTitle}
              </h2>
              <p className="mt-4 leading-relaxed text-fd-muted-foreground sm:text-lg">
                {t.definition}
              </p>
            </div>
            <dl className={cn(surface, 'flex flex-col gap-4 p-5 text-sm')}>
              {t.facts.map((f) => (
                <div key={f.term}>
                  <dt className="font-medium">{f.term}</dt>
                  <dd
                    className={cn(
                      'mt-1 leading-relaxed text-fd-muted-foreground',
                      f.mono && 'font-mono text-xs',
                    )}
                  >
                    {f.detail}
                  </dd>
                </div>
              ))}
            </dl>
          </div>
        </section>

        {/* Headline numbers */}
        <section className="w-full border-t px-4 py-12 sm:px-6">
          <div className="mx-auto max-w-6xl">
            <h2 className="sr-only">{t.numbersTitle}</h2>
            <dl className="grid grid-cols-2 gap-y-8 lg:grid-cols-4">
              {t.numbers.map((n, i) => (
                <div
                  key={n.label}
                  className={cn(
                    'px-4 sm:px-6',
                    // The first column of each row starts at the content edge
                    i % 2 === 0 ? 'ps-0 sm:ps-0' : 'border-s',
                    i === 2 && 'lg:border-s lg:ps-6',
                  )}
                >
                  <dt className="text-xs text-fd-muted-foreground sm:text-sm">
                    {n.label}
                  </dt>
                  <dd className="mt-2 text-2xl font-semibold tracking-tight tabular-nums sm:text-3xl">
                    {n.value}
                  </dd>
                  <dd className="mt-1 text-xs text-fd-muted-foreground">
                    {n.note}
                  </dd>
                </div>
              ))}
            </dl>
            <p className="mt-6 text-xs leading-relaxed text-fd-muted-foreground">
              {t.numbersNote}{' '}
              <Link
                to={`${docs}/performance#benchmarks`}
                className="text-fd-foreground underline underline-offset-2"
              >
                {t.numbersLink}
              </Link>
            </p>
          </div>
        </section>

        {/* Code sample */}
        <Section title={t.codeTitle} text={t.codeText}>
          <div className={cn(surface, 'overflow-hidden')}>
            <div className="flex items-center justify-between border-b px-5 py-3 font-mono text-xs text-fd-muted-foreground">
              <span>Battle.cs</span>
              <span>C#</span>
            </div>
            <Code html={loaderData.hero} />
          </div>
        </Section>

        {/* Why it is fast: each mechanism with its measured ratio */}
        <Section title={t.speedTitle} text={t.speedText}>
          <dl className="grid gap-x-8 gap-y-10 sm:grid-cols-2 lg:grid-cols-4">
            {t.speed.map((item) => (
              // The metric reads first on screen, the title first in the markup (<dt> before <dd>)
              <div key={item.title} className="flex flex-col border-t pt-5">
                <dt className="mt-5 text-sm font-medium">{item.title}</dt>
                <dd className="order-first">
                  <span className="text-3xl font-semibold tracking-tight tabular-nums">
                    {item.metric}
                  </span>
                  <span className="mt-1 block text-xs text-fd-muted-foreground">
                    {item.note}
                  </span>
                </dd>
                <dd className="mt-1.5 text-sm leading-relaxed text-fd-muted-foreground">
                  {item.text}
                </dd>
              </div>
            ))}
          </dl>
          <p className="mt-10">
            <MoreLink to={`${docs}/why-fast`}>{t.speedLink}</MoreLink>
          </p>
        </Section>

        {/* How it works */}
        <Section title={t.howTitle} text={t.howText}>
          <ol className={cn(surface, 'divide-y overflow-hidden')}>
            {t.steps.map((s, i) => (
              <li
                key={s.title}
                className="grid min-w-0 md:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]"
              >
                <div className="flex gap-4 p-5 md:p-6">
                  <span className="font-mono text-xs leading-6 text-fd-muted-foreground">
                    {String(i + 1).padStart(2, '0')}
                  </span>
                  <div>
                    <h3 className="font-medium">{s.title}</h3>
                    <p className="mt-1.5 text-sm leading-relaxed text-fd-muted-foreground">
                      {s.text}
                    </p>
                  </div>
                </div>
                <div className="flex min-w-0 items-center border-t bg-fd-background/60 md:border-t-0 md:border-s">
                  <Code html={loaderData.steps[i]} className="w-full" />
                </div>
              </li>
            ))}
          </ol>
        </Section>

        {/* Features: Fumadocs' own cards, the same as the docs pages' */}
        <Section title={t.featuresTitle} text={t.featuresText}>
          <Cards className="gap-4 lg:grid-cols-3">
            {t.features.map((f) => (
              <Card
                key={f.href}
                href={`${docs}/${f.href}`}
                icon={<f.icon />}
                title={f.title}
                description={f.text}
              />
            ))}
          </Cards>
        </Section>

        {/* Built for servers */}
        <Section title={t.serversTitle} text={t.serversText}>
          <ul className="grid gap-x-10 gap-y-5 sm:grid-cols-2">
            {t.serverPoints.map((point) => (
              <li key={point} className="flex gap-3 text-sm leading-relaxed">
                <Check
                  className="mt-0.5 size-4 shrink-0 text-fd-primary"
                  aria-hidden="true"
                />
                <span>{point}</span>
              </li>
            ))}
          </ul>
        </Section>

        {/* FAQ: the same text is the page's FAQPage JSON-LD (meta) */}
        <Section id="faq" title={t.faqTitle}>
          <div className={cn(surface, 'divide-y')}>
            {t.faq.map((item) => (
              // <details>: the answer stays in the HTML (crawlers read it) and opens on click.
              <details key={item.question} className="group">
                <summary className="flex cursor-pointer list-none items-center justify-between gap-4 rounded-xl px-5 py-4 transition-colors hover:bg-fd-accent/40 [&::-webkit-details-marker]:hidden">
                  <h3 className="text-sm font-medium">{item.question}</h3>
                  <ChevronDown
                    className="size-4 shrink-0 text-fd-muted-foreground transition-transform group-open:rotate-180"
                    aria-hidden="true"
                  />
                </summary>
                <p className="px-5 pb-5 text-sm leading-relaxed text-fd-muted-foreground">
                  {item.answer}
                  {item.more && (
                    <>
                      {' '}
                      <Link
                        to={`${docs}/${item.more.href}`}
                        className="text-fd-foreground underline underline-offset-4"
                      >
                        {item.more.text}
                      </Link>
                    </>
                  )}
                </p>
              </details>
            ))}
          </div>
          <p className="mt-6">
            <MoreLink to={`${docs}/faq`}>{t.allFaq}</MoreLink>
          </p>
        </Section>

        {/* Call to action */}
        <section className="w-full px-4 pb-20 sm:px-6 md:pb-28">
          <div
            className={cn(
              surface,
              'mx-auto flex max-w-6xl flex-col gap-8 p-8 md:flex-row md:items-end md:justify-between md:p-12',
            )}
          >
            <div className="max-w-xl">
              <h2 className="text-2xl font-bold tracking-tight sm:text-3xl">
                {t.ctaTitle}
              </h2>
              <p className="mt-3 leading-relaxed text-fd-muted-foreground">
                {t.ctaText}
              </p>
            </div>
            <div className="flex flex-wrap gap-3">
              <Link to={`${docs}/getting-started`} className={button()}>
                <BookOpen />
                {t.ctaStart}
              </Link>
              <Link to={`${docs}/example`} className={button('secondary')}>
                {t.ctaExample}
              </Link>
              <a
                href={lang === 'zh' ? '/llms-full.zh.txt' : '/llms-full.txt'}
                className={button('secondary')}
              >
                <Bot />
                {t.ctaLlms}
              </a>
            </div>
          </div>
        </section>
      </main>
    </HomeLayout>
  );
}
