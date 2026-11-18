#!/usr/bin/env bash
# Builds the engine DLLs the Unity package ships and copies them into src/Editor/Plugins.
#
#   tools/build-unity-plugins.sh
#
# The package contains compiled DLLs because the engine is written in a newer C# than Unity compiles.
# Run this after changing anything under dotnet/UGS.Core or dotnet/UGS.Csv.Sep, and commit the result.
#
#   src/Editor/Plugins/UGS.Core.dll            netstandard2.1, loaded by every supported editor
#   src/Editor/Plugins/CoreClr/*.dll           the Sep CSV backend, loaded only by the Unity 7 (CoreCLR) editor
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
plugins="$root/src/Editor/Plugins"
dotnet="${DOTNET:-dotnet}"

"$dotnet" build "$root/dotnet/UGS.Csv.Sep/UGS.Csv.Sep.csproj" -c Release -v q --nologo

mkdir -p "$plugins/CoreClr"
cp "$root/dotnet/UGS.Core/bin/Release/netstandard2.1/UGS.Core.dll" "$plugins/UGS.Core.dll"
for name in UGS.Csv.Sep Sep csFastFloat; do
  cp "$root/dotnet/UGS.Csv.Sep/bin/Release/net8.0/$name.dll" "$plugins/CoreClr/$name.dll"
done

ls -l "$plugins"/*.dll "$plugins"/CoreClr/*.dll
