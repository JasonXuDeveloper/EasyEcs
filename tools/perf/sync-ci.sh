#!/usr/bin/env bash
# The site's numbers from a run of the Benchmarks workflow (.github/workflows/benchmarks.yml; owner, ledger L17: the
# published numbers come from CI): downloads each runtime's artifact (benchmarks-net10.0, benchmarks-net11.0) of the
# run and makes its reports that runtime's whole set in docs/src/data/bench.json (sync-benchmarks --replace: CI's
# machine and SDK replace the committed set's), then lists the rows that moved (tools/perf/moved.py). Check the diff and
# commit the JSON.
#
#   tools/perf/sync-ci.sh <run id>             both runtimes, each set replaced (a run of --site, the release step)
#   tools/perf/sync-ci.sh <run id> --merge     the classes of a run of a few classes merged into the sets (refused when
#                                              the run's machine or SDK differs from the committed set's)
#   tools/perf/sync-ci.sh <run id> --partial   replace the sets although the run lacks classes they have (a class
#                                              that failed in the run is dropped from the site)
#
# Needs gh (authenticated for the repository) and bun. A runtime whose artifact the run lacks is skipped with a message.
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
for tfm in net10.0 net11.0; do
  if ! gh run download "$run" -n "benchmarks-$tfm" -D "$work/$tfm" >/dev/null 2>&1; then
    echo "run $run has no artifact benchmarks-$tfm: $tfm skipped"
    continue
  fi
  # The artifact holds <tfm>/results (the reports) and done/<tfm>/*.log (bench.sh's logs per class)
  (cd docs && BENCH_RESULTS="$work/$tfm/$tfm/results" bun scripts/sync-benchmarks.ts --runtime "$tfm" "${mode[@]}")
  python3 -I tools/perf/moved.py "$before" docs/src/data/bench.json "$tfm"
done
