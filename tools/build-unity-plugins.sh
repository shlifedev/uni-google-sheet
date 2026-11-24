#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
plugins="$root/src/Editor/Plugins"
dotnet="${DOTNET:-dotnet}"

"$dotnet" build "$root/dotnet/HolySheet.Csv.Sep/HolySheet.Csv.Sep.csproj" -c Release -v q --nologo

mkdir -p "$plugins/CoreClr"
for name in HolySheet.Csv.Sep Sep csFastFloat; do
  cp "$root/dotnet/HolySheet.Csv.Sep/bin/Release/net8.0/$name.dll" "$plugins/CoreClr/$name.dll"
done

ls -l "$plugins"/CoreClr/*.dll
