#!/usr/bin/env bash
# Every generated job a runner of the benchmark worlds failed to inline, and the JIT's reason ("too many locals": the
# job's methods declare more than 32 IL locals). Runs tools/perf/Inlining, which calls each benchmark method of the classes
# named (default: KernelBenchmark, WorldSimulationBenchmark, BattleServerBenchmark, MultiWorldBenchmark,
# FusionBenchmark, LookupGatherBenchmark; a name may leave out "Benchmark") once without tiered compilation, so each
# runner is compiled optimized with Tier1's inlining decisions. Exit code 1 if any job is called, 2 if the build fails
# (tools/build.sh prints its errors). --no-build runs what the solution's Release build left (CI).
#
#   tools/perf/inlining.sh [--no-build] [benchmark class names...]
set -euo pipefail
cd "$(dirname "$0")/../.."
build=()
if [ "${1:-}" = --no-build ]; then build=(--no-build); shift; fi
dll=$(tools/build.sh "${build[@]}" tools/perf/Inlining)
DOTNET_TieredCompilation=0 dotnet "$dll" "$@"
