#!/usr/bin/env bash
# The machine code the JIT writes for a [ForEach] method's job, to check a vector loop (no call in the loop body, no
# range check the loop does not need). The job of method Move is MoveJob0 in the generated __ForEach class
# (World.DescribeSchedule() says which loop each method got). Its Execute is AggressiveInlining: once optimized it is
# part of the World loop that runs it, EasyEcs.World:Masked[..., MoveJob0] for a vector method on one thread, a ForEachBatch*
# runner in parallel; 'EasyEcs.World:Masked' lists every vector method's, '*MoveJob0:Execute*' the job's own (Tier0) code.
#
#   tools/jitdisasm.sh <project> <method pattern> [program arguments...]
#   FULLOPTS=1 tools/jitdisasm.sh samples/HelloWorld 'EasyEcs.World:Masked'
#
# The program runs built in Release with DOTNET_JitDisasm set to the pattern and DOTNET_TC_CallCountingDelayMs=0, which
# promotes a method to Tier1 as soon as it has run 30 times (a game loop does). For a program that runs it a few times
# only, FULLOPTS=1 sets DOTNET_TieredCompilation=0: optimized code at once (Tier1 without its profile).
set -euo pipefail
if [ $# -lt 2 ]; then
  echo "usage: $0 <project> <method pattern, e.g. 'EasyEcs.World:Masked'> [program arguments...]" >&2
  exit 2
fi
project=$1
pattern=$2
shift 2
dotnet build -c Release "$project" >/dev/null
target=$(dotnet msbuild "$project" -getProperty:TargetPath -p:Configuration=Release)
tiered=$([ "${FULLOPTS:-0}" = 1 ] && echo 0 || echo 1)
# Only the program gets the settings (not the SDK that builds it).
DOTNET_JitDisasm="$pattern" DOTNET_TC_CallCountingDelayMs=0 DOTNET_TieredCompilation=$tiered dotnet "$target" "$@"
