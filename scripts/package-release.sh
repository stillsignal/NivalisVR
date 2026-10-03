#!/usr/bin/env bash
# Builds NivalisVR and assembles the release zip in dist/ (Git Bash on Windows).
#
# Inputs (override with environment variables):
#   BEPINEX_DIR      unpacked BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788 zip (pristine, never run)
#   BEPINEX_LICENSE  BepInEx LICENSE (LGPL-2.1) at commit 5b766a3
#   OPENXR_DIR       folder with openxr_loader.dll (x64) and the OpenXR SDK LICENSE (release 1.1.63)
#
# The zip contains only: BepInEx core + its .NET runtime + doorstop files, our plugin, the OpenXR loader,
# license/notice files and the README. Never game files, interop assemblies, configs or unity-libs.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BEPINEX_DIR="${BEPINEX_DIR:-$ROOT/tools/BepInEx-be.788}"
BEPINEX_LICENSE="${BEPINEX_LICENSE:-$ROOT/tools/BepInEx-LICENSE.txt}"
OPENXR_DIR="${OPENXR_DIR:-$ROOT/tools/openxr-1.1.63}"

VERSION="$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$ROOT/src/NivalisVR/Plugin.cs")"
[ -n "$VERSION" ] || { echo "Could not read Version from Plugin.cs" >&2; exit 1; }

for f in "$BEPINEX_DIR/winhttp.dll" "$BEPINEX_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll" "$BEPINEX_LICENSE" \
         "$OPENXR_DIR/openxr_loader.dll" "$OPENXR_DIR/LICENSE"; do
    [ -f "$f" ] || { echo "Missing input: $f" >&2; exit 1; }
done

echo "Building NivalisVR $VERSION..."
dotnet build "$ROOT/src/NivalisVR" -c Release --nologo -v quiet
PLUGIN_DLL="$ROOT/src/NivalisVR/bin/Release/NivalisVR.dll"

NAME="NivalisVR-$VERSION"
STAGE="$ROOT/dist/$NAME"
ZIP="$ROOT/dist/$NAME.zip"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/BepInEx/plugins/NivalisVR/licenses" "$STAGE/BepInEx/patchers"

# BepInEx (unmodified): core, bundled .NET runtime and the doorstop entry point.
cp -r "$BEPINEX_DIR/BepInEx/core" "$STAGE/BepInEx/"
cp -r "$BEPINEX_DIR/dotnet" "$STAGE/"
cp "$BEPINEX_DIR/winhttp.dll" "$BEPINEX_DIR/doorstop_config.ini" "$BEPINEX_DIR/.doorstop_version" "$STAGE/"

# Our plugin + the OpenXR loader.
cp "$PLUGIN_DLL" "$STAGE/BepInEx/plugins/NivalisVR/"
cp "$OPENXR_DIR/openxr_loader.dll" "$STAGE/BepInEx/plugins/NivalisVR/"

# Documentation and licenses.
cp "$ROOT/README.md" "$STAGE/NivalisVR-README.txt"
cp "$ROOT/LICENSE" "$STAGE/BepInEx/plugins/NivalisVR/licenses/NivalisVR-LICENSE.txt"
cp "$ROOT/THIRD-PARTY-NOTICES.md" "$STAGE/BepInEx/plugins/NivalisVR/licenses/"
cp "$BEPINEX_LICENSE" "$STAGE/BepInEx/plugins/NivalisVR/licenses/BepInEx-LICENSE.txt"
cp "$OPENXR_DIR/LICENSE" "$STAGE/BepInEx/plugins/NivalisVR/licenses/OpenXR-SDK-LICENSE.txt"

# Windows' bsdtar writes zip archives (Git Bash's GNU tar can't).
WIN_TAR="/c/Windows/System32/tar.exe"
( cd "$STAGE" && "$WIN_TAR" -a -c -f "$(cygpath -w "$ZIP")" * .doorstop_version )

echo "Created $ZIP"
"$WIN_TAR" -t -f "$(cygpath -w "$ZIP")" | grep -v '/$' | grep -v '^dotnet/' | grep -v '^BepInEx/core/'
echo "(+ $( "$WIN_TAR" -t -f "$(cygpath -w "$ZIP")" | grep -c '^BepInEx/core/.*[^/]$') files in BepInEx/core, $( "$WIN_TAR" -t -f "$(cygpath -w "$ZIP")" | grep -c '^dotnet/.*[^/]$') files in dotnet/)"
du -h "$ZIP" | cut -f1
