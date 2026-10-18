#!/usr/bin/env bash
# Runs every example against the sources of this repository instead of the NuGet package: each file is copied to a
# scratch directory with its `#:package EasyEcs@<version>` line rewritten to `#:project` of src/EasyEcs.Core, next to
# a Directory.Build.props that adds the generator as an analyzer (a project reference does not carry it the way the
# package does) and turns warnings into errors. Fails if an example's package version is not the repository's, or if
# one fails to build or exits with an error.
#   examples/run.sh [example.cs ...]       (default: every examples/*.cs)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$root/Directory.Build.props")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cat > "$work/Directory.Build.props" <<PROPS
<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$root/src/EasyEcs.Generator/EasyEcs.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
PROPS

if [ $# -eq 0 ]; then set -- "$root"/examples/*.cs; fi
failed=0
for example in "$@"; do
  name="$(basename "$example")"
  if [ "$(head -n 1 "$example")" != "#:package EasyEcs@$version" ]; then
    echo "::error file=examples/$name,line=1::the first line must be '#:package EasyEcs@$version'"
    failed=1; continue
  fi
  sed "1s|.*|#:project $root/src/EasyEcs.Core/EasyEcs.Core.csproj|" "$example" > "$work/$name"
  echo "=== $name"
  if ! dotnet run "$work/$name"; then
    echo "::error file=examples/$name::the example failed"
    failed=1
  fi
done
exit $failed
