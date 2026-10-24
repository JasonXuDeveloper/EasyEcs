#!/usr/bin/env bash
# Every source file of a directory with a file map is named in it (src/*/AGENTS.md; CodeFixes in the root AGENTS.md).
set -euo pipefail
cd "$(dirname "$0")/.."
missing=0
check() { # check <dir> <map>
  for f in "$1"/*.cs; do
    name=$(basename "$f")
    grep -qF "$name" "$2" || { echo "$f is not in $2"; missing=1; }
  done
}
check src/EasyEcs.Core src/EasyEcs.Core/AGENTS.md
check src/EasyEcs.Generator src/EasyEcs.Generator/AGENTS.md
check src/EasyEcs.CodeFixes AGENTS.md
[ "$missing" = 0 ] && echo "file maps: 0 problems"
exit "$missing"
