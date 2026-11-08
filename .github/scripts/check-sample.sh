#!/usr/bin/env bash
# Runs a sample and compares what it prints with the "## Expected output" block of its README, and with the output
# block ("## ... [#output]") of its docs pages (docs/content/samples/<kebab-name>.mdx and .zh.mdx), so neither can go
# stale.
#   .github/scripts/check-sample.sh samples/SaveLoad [-- args...]
# Pass extra `dotnet run` options (such as --no-build) in DOTNET_RUN_OPTIONS. A Godot project (a project.godot next to
# the README) is built and run headless by the editor binary in GODOT.
# A sample whose numbers depend on timing (GameLoop: times, and messages from a clients thread) passes a sed -E script
# in SAMPLE_MASK; it rewrites the expected, the actual and the pages' output alike before they are compared, so what
# stays (system names, the lines and their order, the checks) is still checked.
set -euo pipefail

sample="$1"
shift
readme="$sample/README.md"
project="$sample"
# A sample of several projects (HotReload) runs its host.
if ! ls "$project"/*.csproj >/dev/null 2>&1; then
  project=$(ls -d "$sample"/*.Host | head -n 1)
fi

# block <file> <heading-regex>: the first ```text fence of the section whose "## " heading matches, masked.
block() {
  # (the regex goes through the environment: awk -v would read its backslashes as escapes)
  heading="$2" awk '
    $0 ~ ENVIRON["heading"] { section = 1; next }
    section && /^## / { exit }
    section && /^```text/ { fence = 1; next }
    fence && /^```/ { exit }
    fence { print }
  ' "$1" | mask
}
mask() { sed -E "${SAMPLE_MASK:-}"; }

expected=$(block "$readme" '^## Expected output')
if [ -z "$expected" ]; then
  echo "::error file=$readme::no \"## Expected output\" text block"
  exit 1
fi

if [ -f "$sample/project.godot" ]; then
  # Godot runs the Debug build of the project's assembly; its banner line and blank lines are not the sample's output.
  dotnet build "$sample" -nologo -v q
  actual=$("${GODOT:?set GODOT to the Godot .NET editor binary}" --headless --path "$sample" -- "$@" \
    | grep -v -e '^Godot Engine v' -e '^$' | mask)
else
  # shellcheck disable=SC2086
  actual=$(dotnet run ${DOTNET_RUN_OPTIONS:-} -c Release --project "$project" "$@" | mask)
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
  shown=$(block "$page" '^## .*\[#output\]')
  [ -z "$shown" ] && continue
  if [ "$shown" != "$expected" ]; then
    echo "::error file=$page::the output on $page differs from $readme"
    diff <(echo "$expected") <(echo "$shown") || true
    exit 1
  fi
  echo "$sample: output matches $page"
done
