#!/usr/bin/env bash
# Every generated job a runner of the benchmark worlds failed to inline, and the JIT's reason ("too many locals": the
# job's methods declare more than 32 IL locals). Runs tools/Inlining, which calls each benchmark method of the classes
# named (default: KernelBenchmark, WorldSimulation, BattleServer, MultiWorld, Fusion, LookupGather) once without tiered
# compilation, so each runner is compiled optimized with Tier1's inlining decisions. Exit code 1 if any job is called.
#
#   tools/inlining.sh [benchmark class names...]
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build -c Release tools/Inlining >/dev/null
DOTNET_TieredCompilation=0 dotnet tools/Inlining/bin/Release/net10.0/Inlining.dll "$@"
