#!/usr/bin/env bash
# Prints the hash of the sources the live demo's bundle is built from (EasyEcs.Core, the generator, demo/EasyEcs.Demo,
# Directory.Build.props): the blob id and path of each tracked file, hashed. build-demo.sh writes it beside the bundle
# (public/demo/source.sha256), and CI fails when the working tree's hash differs, so a change to the core or the demo
# cannot leave the committed bundle stale. With a revision, the hash of that commit's tree instead.
#   docs/scripts/demo-source.sh [rev]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
paths=(src/EasyEcs.Core src/EasyEcs.Generator demo/EasyEcs.Demo Directory.Build.props)
if [ $# -gt 0 ]; then
  git ls-tree -r --format='%(objectname) %(path)' "$1" -- "${paths[@]}"
else
  # the working tree's contents (what dotnet publish builds), not the index
  files=$(git ls-files -- "${paths[@]}")
  paste -d' ' <(echo "$files" | git hash-object --stdin-paths) <(echo "$files")
fi | LC_ALL=C sort -k2 | sha256sum | cut -d' ' -f1
