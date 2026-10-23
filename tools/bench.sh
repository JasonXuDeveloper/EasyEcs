#!/usr/bin/env bash
# Runs benchmark classes one BenchmarkDotNet process per class, resumable, and merges them into the site's numbers.
#
#   tools/bench.sh --site [--sync]          the classes the site shows (the classes of docs/src/data/bench.json)
#   tools/bench.sh Class... [--sync]        the given classes (short names: BulkBenchmark, Features.RemovalComparison)
#   tools/bench.sh --all [--sync]           every class of both projects
#
# A class that finished is marked in BenchmarkDotNet.Artifacts/done/ and skipped by the next run, so a run cut short
# (a container restart) goes on where it stopped; --fresh clears the marks. Each project is built once, the classes
# run with --no-build. One run at a time on a machine: the runs share a lock (BENCH_LOCK, flock -o, so a build server
# left behind does not keep it). --sync merges the reports into docs/src/data/bench.json (sync-benchmarks --merge):
# the classes run replace theirs, the others stay.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
art=$root/BenchmarkDotNet.Artifacts
lock=${BENCH_LOCK:-/tmp/easyecs-bench.lock}
# The whole run holds the lock; -o keeps its descriptor out of the commands, so nothing they leave behind holds it.
[ -n "${EASYECS_BENCH_LOCKED:-}" ] || EASYECS_BENCH_LOCKED=1 exec flock -o "$lock" "$0" "$@"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

sync=0 fresh=0 classes=()
for arg in "$@"; do
  case $arg in
    --sync) sync=1 ;;
    --fresh) fresh=1 ;;
    --site) mapfile -t -O "${#classes[@]}" classes < <(python3 -c '
import json, sys
for name, entry in json.load(open(sys.argv[1]))["benchmarks"].items():
    print(f"{entry[\"namespace\"]}.{name}" if entry.get("namespace") else name)' "$root/docs/src/data/bench.json") ;;
    --all) for p in EasyEcs.Benchmark EasyEcs.Comparison; do
             mapfile -t -O "${#classes[@]}" classes < <(dotnet run -c Release --project "$root/benchmarks/$p" -- --list flat 2>/dev/null \
               | grep -E '^[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_]*$' | sed 's/\.[^.]*$//' | sort -u)
           done ;;
    -*) echo "unknown option $arg" >&2; exit 2 ;;
    *) classes+=("$arg") ;;
  esac
done
[ ${#classes[@]} -gt 0 ] || { sed -n '2,12p' "$0"; exit 2; }
[ $fresh = 1 ] && rm -rf "$art/done"
mkdir -p "$art/done"

project() { case $1 in EcsComparison.*|Features.*|*Comparison*) echo EasyEcs.Comparison ;; *) echo EasyEcs.Benchmark ;; esac; }

cd "$root"
built=""
for cls in "${classes[@]}"; do
  [ -f "$art/done/$cls" ] && continue
  p=$(project "$cls")
  if [[ " $built " != *" $p "* ]]; then
    dotnet build -c Release "benchmarks/$p" -v q -nologo > "$art/done/build-$p.log" 2>&1 || { cat "$art/done/build-$p.log"; exit 1; }
    built="$built $p"
  fi
  echo "$(date +%T) $cls"
  # A short name matches after a dot and before the method: BufferChurnBenchmark is not ParallelBufferChurnBenchmark.
  case $cls in EasyEcs.*|EcsComparison.*) filter="$cls.*" ;; *) filter="*.$cls.*" ;; esac
  dotnet run -c Release --no-build --project "benchmarks/$p" -- --filter "$filter" --buildTimeout 900 > "$art/done/$cls.log" 2>&1 \
    && touch "$art/done/$cls" || echo "  failed: $art/done/$cls.log"
done
echo "$(date +%T) done"
if [ $sync = 1 ]; then
  (cd docs && BENCH_RESULTS="$art/results" bun scripts/sync-benchmarks.ts --merge)
fi
