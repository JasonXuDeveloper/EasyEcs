#!/usr/bin/env bash
# The site's numbers from a run of the Benchmarks workflow (.github/workflows/benchmarks.yml; owner, ledger L17: the
# published numbers come from CI): downloads the run's artifact (benchmarks) and makes its reports the whole set of
# docs/src/data/bench.json (sync-benchmarks --replace: CI's machine and SDK replace the committed set's), then lists
# the rows that moved (tools/perf/moved.py). Check the diff and commit the JSON.
#
#   tools/perf/sync-ci.sh <run id>             the set replaced (a run of --site, the release step)
#   tools/perf/sync-ci.sh <run id> --merge     the classes of a run of a few classes merged into the set (refused when
#                                              the run's machine or SDK differs from the committed set's)
#   tools/perf/sync-ci.sh <run id> --partial   replace the set although the run lacks classes it has (a class that
#                                              failed in the run is dropped from the site)
#
# Needs gh (authenticated for the repository) and bun.
set -euo pipefail
if [ $# -lt 1 ]; then sed -n '2,13p' "$0"; exit 2; fi
run=$1
shift
mode=(--replace)
for arg in "$@"; do
  case $arg in
    --merge) mode=(--merge) ;;
    --partial) mode=(--replace --partial) ;;
    *) echo "unknown option $arg" >&2; exit 2 ;;
  esac
done
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$root"
before=$work/before.json
cp docs/src/data/bench.json "$before"
gh run download "$run" -n benchmarks -D "$work/run"
# The artifact holds results/ (the reports) and done/*.log (bench.sh's logs per class)
(cd docs && BENCH_RESULTS="$work/run/results" bun scripts/sync-benchmarks.ts "${mode[@]}")
python3 -I tools/perf/moved.py "$before" docs/src/data/bench.json
