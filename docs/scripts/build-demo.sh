#!/usr/bin/env bash
# Publishes demo/EasyEcs.Demo (.NET in WebAssembly) into public/demo/_framework, the files the demo page loads.
# Cloudflare Pages builds the site with Bun only, so the bundle is committed: run this after changing the demo or
# EasyEcs.Core, then commit public/demo (with source.sha256: the hash of the sources it was built from, which CI
# compares with demo-source.sh). Needs the repository's SDK (global.json, .NET 11) and the .NET 10 runtime packs it
# downloads; the demo targets net10.0 (the LTS), so the workload is wasm-tools-net10 (Mono AOT for net10.0: the IL
# compiled ahead of time to WebAssembly), installed when missing.
set -euo pipefail

if ! dotnet workload list | grep -q '^wasm-tools-net10 '; then
  echo "Installing the wasm-tools-net10 workload (Mono AOT for WebAssembly, net10.0)"
  dotnet workload install wasm-tools-net10
fi

docs="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$docs/../demo/EasyEcs.Demo/EasyEcs.Demo.csproj"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

dotnet publish "$project" -c Release -o "$out" -nologo

target="$docs/public/demo/_framework"
rm -rf "$target"
mkdir -p "$target"
cp "$out"/wwwroot/_framework/* "$target"/
# The runtime pack ships some files executable: the site serves them as data
chmod 644 "$target"/*
"$docs/scripts/demo-source.sh" > "$docs/public/demo/source.sha256"

bytes=$(cat "$target"/* | wc -c)
echo "public/demo/_framework: $(ls "$target" | wc -l) files, $((bytes / 1024)) KiB (Cloudflare serves it compressed)"
