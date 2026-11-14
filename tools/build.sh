#!/usr/bin/env bash
# Builds a project in Release for the tool scripts and prints the path of its assembly (TargetPath). A failed build
# prints its errors and exits 2: a stale build must never pass for a checked one. --no-build skips the build (CI, after
# the solution's Release build) and only prints the path.
#
#   dll=$(tools/build.sh [--no-build] <project>)
set -euo pipefail
build=1
if [ "${1:-}" = --no-build ]; then build=0; shift; fi
project=$1
if [ $build = 1 ] && ! log=$(dotnet build -c Release "$project" 2>&1); then
  # dotnet writes its errors to stdout: print them (all of the log when none matches)
  printf '%s\n' "$log" | grep -E ' error |error:' | sort -u >&2 || printf '%s\n' "$log" >&2
  echo "tools/build.sh: the build of $project failed" >&2
  exit 2
fi
dotnet msbuild "$project" -getProperty:TargetPath -p:Configuration=Release
