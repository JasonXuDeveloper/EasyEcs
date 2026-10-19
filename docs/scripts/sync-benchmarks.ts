/**
 * Writes src/data/bench.json, the only source of the benchmark numbers the
 * site shows, from BenchmarkDotNet's `*-report.csv` files (and the machine
 * from the matching `*-report-github.md` headers).
 *
 * Input: the directory in BENCH_RESULTS, else the repository's
 * BenchmarkDotNet.Artifacts/results (where `dotnet run -c Release --project
 * benchmarks/EasyEcs.Benchmark` and `.../EasyEcs.Comparison` write them).
 * `bun run build` runs this first; when the directory is missing (Cloudflare
 * Pages) or holds only part of a run, the committed file is kept, so a
 * filtered local run (`--filter '*Iterate*'`) never replaces the full data
 * with a few rows from another machine.
 *
 *   BENCH_RESULTS=/path/to/BenchmarkDotNet.Artifacts/results bun run sync:benchmarks
 *   bun scripts/sync-benchmarks.ts --dir <results> --out <bench.json> [--partial | --merge]
 *
 * `--partial` writes whatever the directory holds (for a first run, or when a
 * benchmark class was removed on purpose). `--merge` replaces the classes the
 * directory holds and keeps every other committed class (a run of new or
 * changed classes only); it refuses reports from another machine or runtime,
 * whose numbers would not compare with the committed ones.
 */
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

export interface BenchFile {
  source: string;
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
  /** NuGet versions of the libraries the comparison runs against */
  peers: Record<string, string>;
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
): BenchFile['environment'] & { job: string | null } {
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

  const benchmarks: BenchFile['benchmarks'] = {};
  const environments = new Map<string, BenchFile['environment']>();
  for (const file of reports) {
    const full = file.replace(/-report\.csv$/, '');
    const dot = full.lastIndexOf('.');
    const name = full.slice(dot + 1);
    benchmarks[name] = {
      namespace: full.slice(0, Math.max(dot, 0)),
      job: null,
      rows: readReport(path.join(dir, file)),
    };
    const { job, ...env } = readEnvironment(
      path.join(dir, `${full}-report-github.md`),
    );
    benchmarks[name].job = job;
    environments.set(JSON.stringify(env), env);
  }

  if (environments.size > 1)
    console.warn(
      `[bench] the reports come from ${environments.size} different machines or runtimes; the first one is recorded`,
    );

  const missing = Object.keys(committed?.benchmarks ?? {}).filter(
    (name) => !(name in benchmarks),
  );
  if (merge && committed) {
    const env = [...environments.values()][0];
    if (
      environments.size > 1 ||
      JSON.stringify(env) !== JSON.stringify(committed.environment)
    ) {
      console.warn(
        `[bench] --merge: the reports in ${dir} come from another machine or runtime than ${path.relative(docsRoot, out)}; nothing written`,
      );
      process.exitCode = 1;
      return;
    }
    // The committed classes in their order, the new ones replaced or added at the end
    const merged: BenchFile['benchmarks'] = {};
    for (const [name, entry] of Object.entries(committed.benchmarks))
      merged[name] = benchmarks[name] ?? entry;
    for (const [name, entry] of Object.entries(benchmarks))
      merged[name] ??= entry;
    const newest = Math.max(
      Date.parse(committed.generatedAt),
      ...reports.map((f) => statSync(path.join(dir, f)).mtimeMs),
    );
    const data: BenchFile = {
      ...committed,
      generatedAt: new Date(newest).toISOString(),
      peers: readPeers(),
      benchmarks: merged,
    };
    writeFileSync(out, `${JSON.stringify(data, null, 2)}\n`);
    console.log(
      `[bench] merged ${reports.length} class(es) into ${path.relative(docsRoot, out)}: ${Object.keys(benchmarks).join(', ')}`,
    );
    return;
  }
  if (missing.length > 0 && !partial) {
    console.warn(
      `[bench] ${dir} lacks ${missing.length} benchmark class(es) of the committed data (${missing.join(', ')}): keeping ${path.relative(docsRoot, out)}. Pass --partial to write it anyway.`,
    );
    return;
  }

  const data: BenchFile = {
    source: 'BenchmarkDotNet *-report.csv (scripts/sync-benchmarks.ts)',
    // The time of the run, not of this script: the newest report's mtime.
    generatedAt: new Date(
      Math.max(...reports.map((f) => statSync(path.join(dir, f)).mtimeMs)),
    ).toISOString(),
    environment: [...environments.values()][0],
    peers: readPeers(),
    benchmarks,
  };
  writeFileSync(out, `${JSON.stringify(data, null, 2)}\n`);
  const rows = Object.values(benchmarks).reduce((n, b) => n + b.rows.length, 0);
  console.log(
    `[bench] wrote ${path.relative(docsRoot, out)}: ${reports.length} classes, ${rows} rows (${data.environment.cpu}, ${data.environment.runtime})`,
  );
}

main();
