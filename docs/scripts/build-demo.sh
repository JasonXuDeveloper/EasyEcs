#!/usr/bin/env bash
# Publishes demo/EasyEcs.Demo (.NET in WebAssembly) into public/demo/_framework, the files the demo page loads.
# Cloudflare Pages builds the site with Bun only, so the bundle is committed: run this after changing the demo or
# EasyEcs.Core, then commit public/demo. Needs the .NET 10 SDK; the wasm-tools workload (Mono AOT: the IL compiled
# ahead of time to WebAssembly) is installed when missing.
set -euo pipefail

if ! dotnet workload list | grep -q '^wasm-tools '; then
  echo "Installing the wasm-tools workload (Mono AOT for WebAssembly)"
  dotnet workload install wasm-tools
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

bytes=$(cat "$target"/* | wc -c)
echo "public/demo/_framework: $(ls "$target" | wc -l) files, $((bytes / 1024)) KiB (Cloudflare serves it compressed)"
