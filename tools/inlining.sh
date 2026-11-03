#!/usr/bin/env bash
# Every generated job a runner of the benchmark worlds failed to inline, and the JIT's reason ("too many locals": the
# job's methods declare more than 32 IL locals). Runs tools/Inlining, which calls each benchmark method of the classes
# named (default: KernelBenchmark, WorldSimulation, BattleServer, MultiWorld, Fusion, LookupGather) once without tiered
# compilation, so each runner is compiled optimized with Tier1's inlining decisions. Exit code 1 if any job is called,
# 2 if the build fails (its errors printed: a stale build must never pass for a checked one).
#
#   tools/inlining.sh [benchmark class names...]
set -euo pipefail
cd "$(dirname "$0")/.."
if ! log=$(dotnet build -c Release tools/Inlining 2>&1); then
  printf '%s\n' "$log" | grep -E ' error |error:' | sort -u >&2 || printf '%s\n' "$log" >&2
  echo "tools/inlining.sh: the build failed" >&2
  exit 2
fi
DOTNET_TieredCompilation=0 dotnet tools/Inlining/bin/Release/net10.0/Inlining.dll "$@"
