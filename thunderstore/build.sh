#!/usr/bin/env bash
# Build the Thunderstore package into <dist>: ServerTaming-<version>.zip with manifest.json,
# icon.png, README.md and CHANGELOG.md at the root and the DLL in plugins/. The version is the
# plugin's (Version in ServerTamingPlugin.cs). Usage: thunderstore/build.sh <dist>
# (needs dotnet 8, zip and the game's DLLs at the paths in the .csproj).
set -euo pipefail
dist=$(realpath -m "${1:?output folder}")
here=$(cd "$(dirname "$0")" && pwd); repo=$(dirname "$here")
dotnet=${DOTNET:-dotnet}
version=$(sed -n 's/.*const string Version = "\([0-9.]*\)".*/\1/p' "$repo/ServerTamingPlugin.cs")
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "plugin version not found" >&2; exit 1; }
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
"$dotnet" build "$repo/ServerTaming.csproj" -c Release -p:DebugType=none -o "$work/build" >/dev/null
rm -rf "$repo/bin" "$repo/obj"
mkdir -p "$work/pkg/plugins" "$dist"
cp "$work/build/ServerTaming.dll" "$work/pkg/plugins/"
cp "$here/icon.png" "$here/CHANGELOG.md" "$repo/README.md" "$work/pkg/"
sed "s/@VERSION@/$version/g" "$here/manifest.json" > "$work/pkg/manifest.json"
out="$dist/ServerTaming-$version.zip"
rm -f "$out"
(cd "$work/pkg" && zip -qrX "$out" .)
echo "$out"
