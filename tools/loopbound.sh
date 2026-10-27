#!/usr/bin/env bash
# Where each vector loop of a benchmark class is bound (ports, front end or a loop-carried chain), from llvm-mca over
# the Tier1 machine code. Runs tools/LoopBound (each [Benchmark] method of the class called past Tier1, the class's int
# [Params] set to <rows>) with DOTNET_JitDisasm on EasyEcs code, then tools/loopbound.py over the listing: a markdown
# row per job and loop (whole vectors, masked words). Needs llvm-mca (LLVM 18 or later) on the PATH.
#
#   tools/loopbound.sh <benchmark class> [rows=1000] [methods, comma separated]
#   LISTING=out.asm tools/loopbound.sh KernelBenchmark 1000 EnergyGenerated     # keep the listing
#   CPUS=icelake-server,znver4,skylake REG=ymm tools/loopbound.sh KernelBenchmark  # other models, the 256-bit loops
set -euo pipefail
if [ $# -lt 1 ]; then
  echo "usage: $0 <benchmark class> [rows] [methods]" >&2
  exit 2
fi
cd "$(dirname "$0")/.."
listing=${LISTING:-$(mktemp --suffix=.asm)}
dotnet build -c Release tools/LoopBound >/dev/null
rm -f "$listing"
DOTNET_JitDisasm='EasyEcs.*:*' DOTNET_TC_CallCountingDelayMs=0 DOTNET_JitStdOutFile="$listing" \
  dotnet tools/LoopBound/bin/Release/net10.0/LoopBound.dll "$1" "${2:-1000}" 300 ${3:+"$3"} >/dev/null
python3 -I tools/loopbound.py "$listing" --reg "${REG:-zmm}" --cpus "${CPUS:-icelake-server,znver4}"
