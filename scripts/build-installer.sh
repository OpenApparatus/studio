#!/usr/bin/env bash
# Build a Velopack installer locally on macOS / Linux.
#
# Usage:
#   ./scripts/build-installer.sh 0.1.0                # auto-detects RID
#   ./scripts/build-installer.sh 0.1.0 osx-arm64      # explicit RID
#
# Output: ./releases/OpenApparatusStudio-<channel>-Setup.{pkg,exe} + update .nupkg

set -euo pipefail

VERSION="${1:?Usage: build-installer.sh <version> [rid]}"
RID="${2:-}"

if [[ -z "$RID" ]]; then
    case "$(uname -sm)" in
        "Darwin arm64")  RID="osx-arm64" ;;
        "Darwin x86_64") RID="osx-x64" ;;
        "Linux x86_64")  RID="linux-x64" ;;
        *) echo "Unsupported platform: $(uname -sm). Pass RID explicitly."; exit 1 ;;
    esac
fi

CHANNEL="$RID"
case "$RID" in
    win-*) MAIN_EXE="OpenApparatus.Studio.exe" ;;
    *)     MAIN_EXE="OpenApparatus.Studio" ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

PROJECT="src/OpenApparatus.Studio/OpenApparatus.Studio.csproj"

if ! command -v vpk >/dev/null 2>&1; then
    echo "Installing Velopack CLI (vpk)..."
    dotnet tool install -g vpk
    export PATH="$PATH:$HOME/.dotnet/tools"
fi

echo "Publishing $RID (self-contained)..."
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o publish

echo "Packing v$VERSION..."
vpk pack \
    --packId OpenApparatusStudio \
    --packTitle "OpenApparatus Studio" \
    --packAuthors OpenApparatus \
    --packVersion "$VERSION" \
    --packDir publish \
    --mainExe "$MAIN_EXE" \
    --channel "$CHANNEL" \
    --outputDir releases

echo
echo "Done. Artifacts in ./releases:"
ls -lh releases
