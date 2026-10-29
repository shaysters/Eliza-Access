#!/usr/bin/env bash
# Builds everything a GitHub release needs into release/:
#   release/changes-latest.md        release notes
#   release/assets/ElizaAccess.zip   the mod (flat; extracts into the game folder)
#   release/assets/ElizaAccessInstaller.exe   branded Loadstone installer
#   release/assets/loadstone-release.json     Loadstone release manifest
# Then prints the gh command to publish. Nothing is uploaded by this script.
#
# The version comes from <Version> in ElizaAccess-v2.csproj; bump it and
# changes-latest.md before running.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ElizaAccess-v2.csproj | head -1)
if [ -z "$VERSION" ]; then
    echo "No <Version> in ElizaAccess-v2.csproj" >&2
    exit 1
fi

echo "=== Building mod v$VERSION (Release) ==="
# SkipDeploy: a release build shouldn't touch the local game install.
dotnet build ElizaAccess-v2.csproj -c Release -p:SkipDeploy=true

# --- Loadstone: branded standalone installer + release manifest -------------
# Loadstone lives in the loadstone/ git submodule (override with
# MODMANAGER_DIR to use another checkout). Building the installer exe needs
# the wx/CMake toolchain from a VS developer environment; WX_DIR points at
# the static wxWidgets install.
MODMANAGER_DIR="${MODMANAGER_DIR:-$SCRIPT_DIR/loadstone}"
WX_DIR="${WX_DIR:-C:/Users/bradj/code/wx-static}"

if [ ! -f "$MODMANAGER_DIR/Cargo.toml" ]; then
    echo "=== Fetching loadstone submodule ==="
    git -C "$SCRIPT_DIR" submodule update --init loadstone
fi

RELEASE_DIR="$SCRIPT_DIR/release"
ASSETS_DIR="$RELEASE_DIR/assets"
rm -rf "$RELEASE_DIR"
mkdir -p "$ASSETS_DIR"
cp changes-latest.md "$RELEASE_DIR/"
cp bin/Release/net472/ElizaAccess.zip "$ASSETS_DIR/"

# The zip already holds ElizaAccess-README.md; add an HTML copy for
# reading in a browser.
echo "=== Adding the README as HTML ==="
uv run --no-project --with markdown==3.7 python scripts/readme_html.py README.md "$ASSETS_DIR/ElizaAccess.zip"

# Git Bash's GNU /usr/bin/link shadows MSVC's link.exe, which breaks cargo
# build scripts (both the installer build, whose CMake step runs cargo for
# the bridge, and the CLI below); put the MSVC bin directory (where cl.exe
# lives) first.
if command -v cl.exe >/dev/null 2>&1; then
    PATH="$(dirname "$(command -v cl.exe)"):$PATH"
fi

echo "=== Building Loadstone standalone installer ==="
cmake -S "$MODMANAGER_DIR/app" -B "$MODMANAGER_DIR/build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_PREFIX_PATH="$WX_DIR" \
    -DLOADSTONE_EMBED_PROFILE="$SCRIPT_DIR/installer/loadstone-profile.json" >/dev/null
cmake --build "$MODMANAGER_DIR/build"
cp "$MODMANAGER_DIR/build/loadstone-manager.exe" "$ASSETS_DIR/ElizaAccessInstaller.exe"

echo "=== Building loadstone-release.json ==="
(cd "$MODMANAGER_DIR" && cargo build --release -p loadstone-cli >/dev/null)
"$MODMANAGER_DIR/target/release/loadstone.exe" package --version "v$VERSION" \
    --manifest loadstone-packages/eliza-access.json \
    --artifact "$ASSETS_DIR/ElizaAccess.zip" \
    --installer "$ASSETS_DIR/ElizaAccessInstaller.exe" \
    --notes-file "$RELEASE_DIR/changes-latest.md" \
    --out "$ASSETS_DIR/loadstone-release.json"

echo "=== Done ==="
echo "Release folder: release/ (notes: changes-latest.md, assets: assets/)"
echo "Publish with:   gh release create v$VERSION --title \"Eliza Access $VERSION\" --notes-file release/changes-latest.md release/assets/*"
