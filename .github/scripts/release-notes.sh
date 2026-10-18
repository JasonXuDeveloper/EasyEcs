#!/usr/bin/env bash
# The body of a GitHub release: the install line, the release's section of the changelog (docs/content/changelog.mdx,
# "## X.Y.Z [#vX-Y-Z]" up to the next "## "; site links made absolute) and every commit since the previous tag, so a
# rebase-merged pull request still shows each of its commits. gh release create --generate-notes appends the pull
# requests and the Full Changelog link after it.
# Usage: release-notes.sh <version>   (run in a clone with the tags and full history)
set -euo pipefail
version="$1"
site="https://ecs.xgamedev.net"

echo "Install: \`dotnet add package EasyEcs --version $version\` · templates: \`dotnet new install EasyEcs.Templates::$version\`"
echo

section=$(awk -v v="$version" '
  $0 ~ "^## " v "( |$)" { on = 1; next }
  on && /^## /          { exit }
  on                    { print }
' docs/content/changelog.mdx | sed -E "s#\]\(/#](${site}/#g")
if [ -n "$(tr -d '[:space:]' <<<"$section")" ]; then
  echo "## Highlights"
  echo "$section"
  echo
  echo "Full notes and docs: ${site}/en/docs/changelog"
  echo
fi

previous=$(git describe --tags --abbrev=0 "v$version^" 2>/dev/null || true)
range="${previous:+$previous..}v$version"
count=$(git rev-list --no-merges --count "$range")
echo "<details><summary>${count} commits since ${previous:-the first commit}</summary>"
echo
git log --no-merges --reverse --format='- %s (%h)' "$range" | grep -v -- '- Bump to v' || true
echo
echo "</details>"
