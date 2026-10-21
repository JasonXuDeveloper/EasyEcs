#!/usr/bin/env bash
# Runs a sample and compares what it prints with the "## Expected output" block of its README, so the README's real
# output cannot go stale. Only for samples whose output does not depend on timing or the machine.
#   .github/scripts/check-sample.sh samples/SaveLoad [-- args...]
# Pass extra `dotnet run` options (such as --no-build) in DOTNET_RUN_OPTIONS.
set -euo pipefail

sample="$1"
shift
readme="$sample/README.md"
project="$sample"
# A sample of several projects (HotReload) runs its host.
if ! ls "$project"/*.csproj >/dev/null 2>&1; then
  project=$(ls -d "$sample"/*.Host | head -n 1)
fi

expected=$(awk '
  /^## Expected output/ { section = 1; next }
  section && /^## / { exit }
  section && /^```text/ { fence = 1; next }
  fence && /^```/ { exit }
  fence { print }
' "$readme")
if [ -z "$expected" ]; then
  echo "::error file=$readme::no \"## Expected output\" text block"
  exit 1
fi

# shellcheck disable=SC2086
actual=$(dotnet run ${DOTNET_RUN_OPTIONS:-} -c Release --project "$project" "$@")
if [ "$actual" != "$expected" ]; then
  echo "::error file=$readme::the output of $sample differs from its README's expected output"
  diff <(echo "$expected") <(echo "$actual") || true
  exit 1
fi
echo "$sample: output matches $readme"
