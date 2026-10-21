#!/usr/bin/env bash
# Runs a sample and compares what it prints with the "## Expected output" block of its README, and with the output
# block ("## ... [#output]") of its docs pages (docs/content/samples/<kebab-name>.mdx and .zh.mdx), so neither can go
# stale. Only for samples whose output does not depend on timing or the machine.
#   .github/scripts/check-sample.sh samples/SaveLoad [-- args...]
# Pass extra `dotnet run` options (such as --no-build) in DOTNET_RUN_OPTIONS. A Godot project (a project.godot next to
# the README) is built and run headless by the editor binary in GODOT.
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

if [ -f "$sample/project.godot" ]; then
  # Godot runs the Debug build of the project's assembly; its banner line and blank lines are not the sample's output.
  dotnet build "$sample" -nologo -v q
  actual=$("${GODOT:?set GODOT to the Godot .NET editor binary}" --headless --path "$sample" -- "$@" \
    | grep -v -e '^Godot Engine v' -e '^$')
else
  # shellcheck disable=SC2086
  actual=$(dotnet run ${DOTNET_RUN_OPTIONS:-} -c Release --project "$project" "$@")
fi
if [ "$actual" != "$expected" ]; then
  echo "::error file=$readme::the output of $sample differs from its README's expected output"
  diff <(echo "$expected") <(echo "$actual") || true
  exit 1
fi
echo "$sample: output matches $readme"

# The docs pages of the sample show the same output.
slug=$(basename "$sample" | sed -E 's/([a-z0-9])([A-Z])/\1-\2/g' | tr '[:upper:]' '[:lower:]')
for page in "docs/content/samples/$slug.mdx" "docs/content/samples/$slug.zh.mdx"; do
  [ -f "$page" ] || continue
  shown=$(awk '
    /^## .*\[#output\]/ { section = 1; next }
    section && /^## / { exit }
    section && /^```text/ { fence = 1; next }
    fence && /^```/ { exit }
    fence { print }
  ' "$page")
  [ -z "$shown" ] && continue
  if [ "$shown" != "$expected" ]; then
    echo "::error file=$page::the output on $page differs from $readme"
    diff <(echo "$expected") <(echo "$shown") || true
    exit 1
  fi
  echo "$sample: output matches $page"
done
