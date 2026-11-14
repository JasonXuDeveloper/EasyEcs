#!/usr/bin/env bash
# Runs benchmark classes one BenchmarkDotNet process per class, resumable, and merges them into the site's numbers.
#
#   tools/perf/bench.sh --site [--sync]          the classes the site shows (the classes of docs/src/data/bench.json, of
#                                                either runtime)
#   tools/perf/bench.sh Class... [--sync]        the given classes (short names: BulkBenchmark, Features.RemovalComparison)
#   tools/perf/bench.sh --all [--sync]           every class of both projects
#   --framework net11.0                          the runtime the classes run on (default net10.0); the site keeps
#                                                numbers per runtime, so --sync merges into that runtime's set
#
# A class that finished is marked in BenchmarkDotNet.Artifacts/done/<tfm>/ and skipped by the next run of that runtime,
# so a run cut short (a container restart) goes on where it stopped; --fresh clears the marks. The reports of a runtime
# go to BenchmarkDotNet.Artifacts/<tfm>/results. Each project is built and lists its benchmarks once; a class runs,
# with --no-build, in the project that declares it. One run at a time on a machine: the runs share a lock (BENCH_LOCK,
# flock -o, so a build server left behind does not keep it). --sync merges the reports into the runtime's numbers in
# docs/src/data/bench.json (sync-benchmarks --runtime <tfm> --merge, refused when the machine or the SDK differs from
# that runtime's committed numbers): the classes run replace theirs, the others stay, and the rows that moved (15 %, or
# their allocation) are listed. Reports from elsewhere (the CI benchmark workflow's artifacts) are merged the same way:
#   (cd docs && BENCH_RESULTS=<download>/net11.0/results bun scripts/sync-benchmarks.ts --runtime net11.0 --merge)
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
art=$root/BenchmarkDotNet.Artifacts
lock=${BENCH_LOCK:-/tmp/bench.lock}
# The whole run holds the lock exclusively; -o keeps its descriptor out of the commands, so nothing they leave behind
# holds it. The gate (held while waiting and running) stops new shared holders, so builds and tests of other sessions,
# which pass the gate before taking the lock shared, cannot starve the run (audit-perf).
[ -n "${EASYECS_BENCH_LOCKED:-}" ] || EASYECS_BENCH_LOCKED=1 exec flock -o "$lock.gate" flock -o "$lock" "$0" "$@"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

sync=0 fresh=0 all=0 classes=() framework=net10.0 next=
for arg in "$@"; do
  if [ "$next" = framework ]; then framework=$arg next=; continue; fi
  case $arg in
    --framework) next=framework ;;
    --sync) sync=1 ;;
    --fresh) fresh=1 ;;
    --site) mapfile -t -O "${#classes[@]}" classes < <(python3 -c '
import json, sys
seen = {}
for numbers in json.load(open(sys.argv[1]))["runtimes"].values():
    for name, entry in numbers["benchmarks"].items():
        ns = entry.get("namespace")
        seen.setdefault(f"{ns}.{name}" if ns else name, None)
print("\n".join(seen))' "$root/docs/src/data/bench.json") ;;
    --all) all=1 ;;
    -*) echo "unknown option $arg" >&2; exit 2 ;;
    *) classes+=("$arg") ;;
  esac
done
if [ $all = 1 ]; then
  for p in EasyEcs.Benchmark EasyEcs.Comparison; do
    mapfile -t -O "${#classes[@]}" classes < <(dotnet run -c Release --framework "$framework" --project "$root/benchmarks/$p" -- --list flat 2>/dev/null \
      | grep -E '^[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_]*$' | sed 's/\.[^.]*$//' | sort -u)
  done
fi
[ ${#classes[@]} -gt 0 ] || { sed -n '2,14p' "$0"; exit 2; }
# A class done on one runtime is not done on the other: the marks are per runtime.
marks=$art/done/$framework
[ $fresh = 1 ] && rm -rf "$marks"
mkdir -p "$marks"

# Each project is built once, and lists its benchmarks (--list flat: Namespace.Class.Method) once.
declare -A listed=()
build() {
  [ -n "${listed[$1]+x}" ] && return
  dotnet build -c Release -f "$framework" "benchmarks/$1" -v q -nologo > "$marks/build-$1.log" 2>&1 || { cat "$marks/build-$1.log"; exit 1; }
  listed[$1]=" $(dotnet run -c Release --no-build --framework "$framework" --project "benchmarks/$1" -- --list flat 2>/dev/null | tr '\n' ' ')"
}
# The project declaring a class (the one whose list has it): a name is never routed by its pattern, so a class of
# either project runs where it lives, and a name neither declares fails here instead of running nothing.
# A short name matches after a dot and before the method: BufferChurnBenchmark is not ParallelBufferChurnBenchmark.
project() {
  for p in EasyEcs.Benchmark EasyEcs.Comparison; do
    if [[ ${listed[$p]} == *" $1."* || ${listed[$p]} == *".$1."* ]]; then echo "$p"; return; fi
  done
  return 1
}

cd "$root"
for cls in "${classes[@]}"; do
  [ -f "$marks/$cls" ] && continue
  build EasyEcs.Benchmark; build EasyEcs.Comparison # here, not in project's subshell, so each is built and listed once
  p=$(project "$cls") || { echo "  failed: no benchmark class $cls in either project"; continue; }
  echo "$(date +%T) $cls ($framework)"
  case $cls in EasyEcs.*|EcsComparison.*) filter="$cls.*" ;; *) filter="*.$cls.*" ;; esac
  log=$marks/$cls.log
  # BenchmarkDotNet exits with 0 when the filter matches nothing: that run is a failure, not a class done.
  if dotnet run -c Release --no-build --framework "$framework" --project "benchmarks/$p" -- --filter "$filter" --buildTimeout 900 \
       --artifacts "$art/$framework" > "$log" 2>&1 \
    && ! grep -q 'No benchmarks to choose from' "$log"; then
    touch "$marks/$cls"
  else
    echo "  failed: $log"
  fi
done
echo "$(date +%T) done"
if [ $sync = 1 ]; then
  before=$(mktemp)
  cp docs/src/data/bench.json "$before"
  (cd docs && BENCH_RESULTS="$art/$framework/results" bun scripts/sync-benchmarks.ts --runtime "$framework" --merge)
  # What moved: a row 15 % slower or faster, or allocating differently, is checked before it is published.
  python3 - "$before" docs/src/data/bench.json "$framework" <<'PY'
import json, sys
old, new = (json.load(open(f))["runtimes"].get(sys.argv[3], {}).get("benchmarks", {}) for f in sys.argv[1:3])
key = lambda r: (r["method"], tuple(sorted(r["params"].items())))
for cls, entry in new.items():
    rows = {key(r): r for r in old.get(cls, {}).get("rows", [])}
    for r in entry["rows"]:
        o = rows.get(key(r))
        if o is None or not o["meanNs"]: continue
        d = r["meanNs"] / o["meanNs"] - 1
        alloc = o["allocatedBytes"] != r["allocatedBytes"]
        if abs(d) > 0.15 or alloc:
            params = ",".join(f"{k}={v}" for k, v in key(r)[1])
            print(f"  {cls}.{r['method']}({params}) {o['meanNs']:.0f} -> {r['meanNs']:.0f} ns {d:+.0%}"
                  + (f", {o['allocatedBytes']} -> {r['allocatedBytes']} B" if alloc else ""))
PY
  rm -f "$before"
fi
