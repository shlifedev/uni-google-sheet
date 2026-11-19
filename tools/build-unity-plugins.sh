#!/usr/bin/env bash
# Builds the optional Sep CSV backend DLLs the Unity package ships and copies them into src/Editor/Plugins/CoreClr.
#
#   tools/build-unity-plugins.sh
#
# The engine itself (src/Editor/Core) ships as source and needs no build step.
# The Sep backend is a DLL because Sep needs .NET 8+, which Unity cannot compile against;
# only the Unity 7 (CoreCLR) editor loads it. Run this after changing dotnet/HolySheet.Csv.Sep, and commit the result.
#
#   src/Editor/Plugins/CoreClr/*.dll           HolySheet.Csv.Sep and its dependencies, loaded only by the Unity 7 (CoreCLR) editor
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
