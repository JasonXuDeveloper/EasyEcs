/**
 * Writes src/data/bench.json, the only source of the benchmark numbers the
 * site shows, from BenchmarkDotNet's `*-report.csv` files (and the machine
 * from the matching `*-report-github.md` headers). The numbers are kept per
 * runtime (`runtimes["net10.0"]`, `runtimes["net11.0"]`): each report goes
 * into the set of the runtime it ran on (its header's `[Host]` line), and the
 * other runtimes' sets are kept as committed.
 *
 * Input: the directory in BENCH_RESULTS, else the repository's
 * BenchmarkDotNet.Artifacts/results (where `dotnet run -c Release --project
 * benchmarks/EasyEcs.Benchmark` and `.../EasyEcs.Comparison` write them;
 * tools/perf/bench.sh writes BenchmarkDotNet.Artifacts/<tfm>/results and
 * passes it). `bun run build` runs this first; when the directory is missing
 * (Cloudflare Pages) or holds only part of a run, the committed file is kept,
 * so a filtered local run (`--filter '*Iterate*'`) never replaces the full
 * data with a few rows from another machine.
 *
 *   BENCH_RESULTS=/path/to/BenchmarkDotNet.Artifacts/results bun run sync:benchmarks
 *   bun scripts/sync-benchmarks.ts --dir <results> --out <bench.json> [--runtime net11.0] [--partial | --merge]
 *
 * `--runtime` takes only the reports of that runtime (the others are skipped
 * with a warning). `--partial` writes whatever the directory holds for a
 * runtime (for a first run, or when a benchmark class was removed on
 * purpose). `--merge` replaces the classes the directory holds and keeps
 * every other committed class of that runtime (a run of new or changed
 * classes only, or a runtime's first numbers); it refuses reports from
 * another machine or SDK than that runtime's committed numbers, whose numbers
 * would not compare with them. A runtime with no numbers yet shows an empty
 * cell on the site, never a guess. */
import {
  existsSync,
  readdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';

const docsRoot = path.resolve(import.meta.dirname, '..');
const repoRoot = path.resolve(docsRoot, '..');

function arg(name: string) {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
}

const dir =
  arg('--dir') ??
  process.env.BENCH_RESULTS ??
  path.join(repoRoot, 'BenchmarkDotNet.Artifacts/results');
const out = arg('--out') ?? path.join(docsRoot, 'src/data/bench.json');
const partial = process.argv.includes('--partial');
const merge = process.argv.includes('--merge');
/** Only the reports of this runtime ("net11.0"); bench.sh passes its --framework */
const only = arg('--runtime');

/* ------------------------------------------------------------------ */
/* Types (src/lib/benchmarks.ts reads the same shape)                  */
/* ------------------------------------------------------------------ */

export interface BenchRow {
  method: string;
  /** [Params] values, e.g. { Entities: "100000" } */
  params: Record<string, string>;
  meanNs: number;
  errorNs: number | null;
  stdDevNs: number | null;
  /** BenchmarkDotNet's Ratio column, against the [Benchmark(Baseline = true)] */
  ratio: number | null;
  /** Bytes allocated per operation; 0 for BenchmarkDotNet's "-" */
  allocatedBytes: number | null;
}

/** The numbers of one runtime */
export interface Runtime {
  /** The time of the newest report */
  generatedAt: string;
  environment: {
    benchmarkDotNet: string | null;
    os: string | null;
    cpu: string | null;
    cores: string | null;
    sdk: string | null;
    runtime: string | null;
    jit: string | null;
  };
  benchmarks: Record<
    string,
    {
      namespace: string;
      /** The job's settings (iterations, launches) from the report header */
      job: string | null;
      rows: BenchRow[];
    }
  >;
}

export interface BenchFile {
  source: string;
  /** NuGet versions of the libraries the comparison runs against */
  peers: Record<string, string>;
  /** By target framework: "net10.0", "net11.0" */
  runtimes: Record<string, Runtime>;
}

/* ------------------------------------------------------------------ */
/* CSV                                                                 */
/* ------------------------------------------------------------------ */

function parseCsv(text: string) {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ',') {
      row.push(cell);
      cell = '';
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      row.push(cell);
      if (row.some((c) => c !== '')) rows.push(row);
      row = [];
      cell = '';
    } else cell += ch;
  }
  row.push(cell);
  if (row.some((c) => c !== '')) rows.push(row);
  return rows;
}

/** BenchmarkDotNet job characteristics: everything else before Mean is a [Params] column */
const characteristics = new Set(
  'Method,Job,AnalyzeLaunchVariance,EvaluateOverhead,MaxAbsoluteError,MaxRelativeError,MinInvokeCount,MinIterationTime,OutlierMode,Affinity,EnvironmentVariables,Jit,LargeAddressAware,Platform,PowerPlanMode,Runtime,AllowVeryLargeObjects,Concurrent,CpuGroups,Force,HeapAffinitizeMask,HeapCount,NoAffinitize,RetainVm,Server,Arguments,BuildConfiguration,Clock,EngineFactory,NuGetReferences,Toolchain,IsMutator,InvocationCount,IterationCount,IterationTime,LaunchCount,MaxIterationCount,MaxWarmupIterationCount,MemoryRandomization,MinIterationCount,MinWarmupIterationCount,RunStrategy,UnrollFactor,WarmupCount,Categories'.split(
    ',',
  ),
);

const timeUnits: Record<string, number> = {
  ns: 1,
  μs: 1e3,
  µs: 1e3,
  us: 1e3,
  ms: 1e6,
  s: 1e9,
};

/** "1,753.9 μs" -> 1753900 */
function parseTime(value: string | undefined) {
  if (!value) return null;
  const match = /^([\d,.]+)\s*(ns|μs|µs|us|ms|s)$/.exec(value.trim());
  if (!match) return null;
  // toPrecision drops float noise (64400.00000000001); BDN prints <= 6 digits
  return Number(
    (Number(match[1].replace(/,/g, '')) * timeUnits[match[2]]).toPrecision(9),
  );
}

const sizeUnits: Record<string, number> = {
  B: 1,
  KB: 1024,
  MB: 1024 ** 2,
  GB: 1024 ** 3,
};

/** "-" -> 0, "67224 B" -> 67224, "2.95 KB" -> 3021 */
function parseBytes(value: string | undefined) {
  if (value === undefined) return null;
  const text = value.trim();
  if (text === '-' || text === '0 B') return 0;
  const match = /^([\d,.]+)\s*(B|KB|MB|GB)$/.exec(text);
  if (!match) return null;
  return Math.round(Number(match[1].replace(/,/g, '')) * sizeUnits[match[2]]);
}

function parseNumber(value: string | undefined) {
  if (!value) return null;
  const n = Number(value.replace(/,/g, ''));
  return Number.isFinite(n) ? n : null;
}

function readReport(file: string) {
  const [header, ...lines] = parseCsv(readFileSync(file, 'utf8'));
  const mean = header.indexOf('Mean');
  if (mean < 0) throw new Error(`${file}: no Mean column`);
  const col = (name: string) => header.indexOf(name);
  const paramColumns = header
    .slice(0, mean)
    .map((name, i) => [name, i] as const)
    .filter(([name]) => !characteristics.has(name));
  const rows: BenchRow[] = [];
  for (const line of lines) {
    const meanNs = parseTime(line[mean]);
    if (meanNs === null) continue; // a failed or empty case
    rows.push({
      method: line[col('Method')],
      params: Object.fromEntries(paramColumns.map(([n, i]) => [n, line[i]])),
      meanNs,
      errorNs: parseTime(line[col('Error')]),
      stdDevNs: parseTime(line[col('StdDev')]),
      ratio: col('Ratio') >= 0 ? parseNumber(line[col('Ratio')]) : null,
      allocatedBytes: parseBytes(line[col('Allocated')]),
    });
  }
  return rows;
}

/** The machine block at the top of a `*-report-github.md` */
function readEnvironment(
  file: string,
): Runtime['environment'] & { job: string | null } {
  const text = existsSync(file) ? readFileSync(file, 'utf8') : '';
  const block = /```\s*\n([\s\S]*?)```/.exec(text)?.[1] ?? '';
  const lines = block.split('\n').map((l) => l.trim());
  const bdn = /^BenchmarkDotNet v([\d.]+),\s*(.+)$/.exec(lines[0] ?? '');
  const cpuLine = lines[1] ?? '';
  const [cpu, ...cpuRest] = cpuLine.split(',').map((s) => s.trim());
  const sdk = lines.find((l) => l.startsWith('.NET SDK'));
  const host = lines.find((l) => l.startsWith('[Host]'));
  const hostMatch = /:\s*(\.NET [\d.]+)[^,]*,\s*(.+)$/.exec(host ?? '');
  const job = lines
    .filter((l) => /^[A-Za-z]+=/.test(l))
    .join(' ')
    .replace(/\s+/g, ' ')
    .trim();
  return {
    benchmarkDotNet: bdn?.[1] ?? null,
    os: bdn?.[2] ?? null,
    cpu: cpu || null,
    cores: cpuRest.join(', ') || null,
    sdk: sdk?.replace('.NET SDK', '').trim() || null,
    runtime: hostMatch?.[1] ?? null,
    jit: hostMatch?.[2] ?? null,
    job: job || null,
  };
}

/** Package versions of the comparison project (Arch, Friflo.Engine.ECS, DefaultEcs) */
function readPeers() {
  const peers: Record<string, string> = {};
  const project = path.join(
    repoRoot,
    'benchmarks/EasyEcs.Comparison/EasyEcs.Comparison.csproj',
  );
  if (!existsSync(project)) return peers;
  const xml = readFileSync(project, 'utf8');
  for (const m of xml.matchAll(
    /<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g,
  ))
    if (m[1] !== 'BenchmarkDotNet') peers[m[1]] = m[2];
  return peers;
}

/** "net10.0" for a report of ".NET 10.0.13" */
function runtimeOf(env: Runtime['environment']) {
  const m = /^\.NET (\d+)\.(\d+)/.exec(env.runtime ?? '');
  return m ? `net${m[1]}.${m[2]}` : null;
}

function main() {
  const committed: BenchFile | undefined = existsSync(out)
    ? JSON.parse(readFileSync(out, 'utf8'))
    : undefined;

  if (!existsSync(dir)) {
    console.log(
      `[bench] ${path.relative(docsRoot, dir) || dir} not found: keeping ${path.relative(docsRoot, out)}`,
    );
    return;
  }
  const reports = readdirSync(dir)
    .filter((f) => f.endsWith('-report.csv'))
    .sort();
  if (reports.length === 0) {
    console.log(
      `[bench] no *-report.csv in ${dir}: keeping the committed data`,
    );
    return;
  }

  // The reports by runtime: each goes into the numbers of the runtime it ran on
  type Read = {
    benchmarks: Runtime['benchmarks'];
    environments: Map<string, Runtime['environment']>;
    files: string[];
  };
  const byRuntime = new Map<string, Read>();
  for (const file of reports) {
    const full = file.replace(/-report\.csv$/, '');
    const dot = full.lastIndexOf('.');
    const name = full.slice(dot + 1);
    const { job, ...env } = readEnvironment(
      path.join(dir, `${full}-report-github.md`),
    );
    const id = runtimeOf(env);
    if (!id) {
      console.warn(`[bench] ${file}: no runtime in its report header, skipped`);
      continue;
    }
    if (only && id !== only) {
      console.warn(`[bench] ${file} ran on ${id}, not ${only}: skipped`);
      continue;
    }
    let read = byRuntime.get(id);
    if (!read) {
      read = { benchmarks: {}, environments: new Map(), files: [] };
      byRuntime.set(id, read);
    }
    read.benchmarks[name] = {
      namespace: full.slice(0, Math.max(dot, 0)),
      job,
      rows: readReport(path.join(dir, file)),
    };
    read.environments.set(JSON.stringify(env), env);
    read.files.push(file);
  }

  const data: BenchFile = {
    source: 'BenchmarkDotNet *-report.csv (scripts/sync-benchmarks.ts)',
    peers: readPeers(),
    runtimes: { ...(committed?.runtimes ?? {}) },
  };
  let wrote = false;
  for (const [id, read] of byRuntime) {
    // The time of the run, not of this script: the newest report's mtime
    const newest = Math.max(
      ...read.files.map((f) => statSync(path.join(dir, f)).mtimeMs),
    );
    const env = [...read.environments.values()][0];
    const before = committed?.runtimes?.[id];
    if (read.environments.size > 1) {
      console.warn(
        `[bench] ${id}: the reports come from ${read.environments.size} different machines or SDKs; nothing written for ${id}`,
      );
      process.exitCode = 1;
      continue;
    }
    if (merge) {
      // Within one runtime the numbers compare only when the machine and the SDK are the same
      if (
        before &&
        JSON.stringify(env) !== JSON.stringify(before.environment)
      ) {
        console.warn(
          `[bench] --merge: the ${id} reports in ${dir} come from another machine or SDK than the committed ${id} numbers; nothing written for ${id}`,
        );
        process.exitCode = 1;
        continue;
      }
      // The committed classes in their order, the new ones replaced or added at the end
      const merged: Runtime['benchmarks'] = {};
      for (const [name, entry] of Object.entries(before?.benchmarks ?? {}))
        merged[name] = read.benchmarks[name] ?? entry;
      for (const [name, entry] of Object.entries(read.benchmarks))
        merged[name] ??= entry;
      data.runtimes[id] = {
        generatedAt: new Date(
          Math.max(newest, before ? Date.parse(before.generatedAt) : 0),
        ).toISOString(),
        environment: env,
        benchmarks: merged,
      };
      console.log(
        `[bench] merged ${read.files.length} class(es) into the ${id} numbers: ${Object.keys(read.benchmarks).join(', ')}`,
      );
      wrote = true;
      continue;
    }
    const missing = Object.keys(before?.benchmarks ?? {}).filter(
      (name) => !(name in read.benchmarks),
    );
    if (!partial && (!before || missing.length > 0)) {
      console.warn(
        before
          ? `[bench] ${dir} lacks ${missing.length} ${id} benchmark class(es) of the committed data (${missing.join(', ')}): keeping the ${id} numbers. Pass --partial to write them anyway, or --merge.`
          : `[bench] ${dir} holds ${id} reports and the committed data has no ${id} numbers: pass --merge (or --partial) to add them.`,
      );
      continue;
    }
    data.runtimes[id] = {
      generatedAt: new Date(newest).toISOString(),
      environment: env,
      benchmarks: read.benchmarks,
    };
    const rows = Object.values(read.benchmarks).reduce(
      (n, b) => n + b.rows.length,
      0,
    );
    console.log(
      `[bench] ${id}: ${read.files.length} classes, ${rows} rows (${env.cpu}, ${env.runtime})`,
    );
    wrote = true;
  }
  if (!wrote) return;
  // The runtimes in the order of their versions
  data.runtimes = Object.fromEntries(
    Object.entries(data.runtimes).sort(([a], [b]) =>
      a.localeCompare(b, 'en', { numeric: true }),
    ),
  );
  writeFileSync(out, `${JSON.stringify(data, null, 2)}\n`);
  console.log(`[bench] wrote ${path.relative(docsRoot, out)}`);
}

main();
