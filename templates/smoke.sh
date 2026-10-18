#!/usr/bin/env bash
# Smoke test of the dotnet new templates against this repository's EasyEcs, never nuget.org's:
# packs EasyEcs and EasyEcs.Templates with a CI version into a local feed, installs the templates from that nupkg
# into a private template hive, creates `easyecs --tests` and an `easyecs-system` in its Systems/ folder, builds,
# runs and tests the result.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="0.0.0-smoke.$(date +%s)"   # a version only the local feed has

dotnet pack "$root/src/EasyEcs.Core/EasyEcs.Core.csproj" -c Release -o "$work/feed" -p:Version="$version" -nologo -v q
dotnet pack "$root/templates/EasyEcs.Templates/EasyEcs.Templates.csproj" -c Release -o "$work/feed" -p:Version="$version" -nologo -v q

export NUGET_PACKAGES="$work/packages"   # no cached package can stand in for the local one
hive=(--debug:custom-hive "$work/hive")
dotnet new install "$work/feed/EasyEcs.Templates.$version.nupkg" "${hive[@]}"

cat > "$work/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="$work/feed"/>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="EasyEcs"/></packageSource>
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
XML

cd "$work"
dotnet new easyecs -n SmokeServer --tests "${hive[@]}"
# The item template goes into Systems/ and takes the project's root namespace without --namespace.
(cd SmokeServer && dotnet new easyecs-system -n RegenSystem -o Systems "${hive[@]}")
grep -q "^namespace SmokeServer;" SmokeServer/Systems/RegenSystem.cs
grep -q "public class RegenSystem : EcsSystem" SmokeServer/Systems/RegenSystem.cs
grep -q "Version=\"$version\"" SmokeServer/SmokeServer.csproj
dotnet build SmokeServer/SmokeServer.Tests/SmokeServer.Tests.csproj -c Release -warnaserror -nologo -v q
dotnet run --project SmokeServer/SmokeServer.csproj -c Release --no-build -- --ticks 60 | tee run.txt
grep -q "stopped after 60 ticks" run.txt
dotnet test SmokeServer/SmokeServer.Tests/SmokeServer.Tests.csproj -c Release --no-build -nologo
echo "templates OK ($version)"
