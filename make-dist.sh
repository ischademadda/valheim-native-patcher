#!/bin/bash
# Builds dist/ValheimPatcher-win-x64.zip: self-contained ValheimPatcher.exe + launchers from windows/
set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> Publishing Windows x64 patcher..."
dotnet publish "$SCRIPT_DIR/Patcher" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -o "$STAGE/pub" --nologo -v q

mkdir -p "$STAGE/ValheimPatcher" "$SCRIPT_DIR/dist"
cp "$STAGE/pub/ValheimPatcher.exe" "$SCRIPT_DIR/windows/"*.cmd "$SCRIPT_DIR/windows/README.txt" "$STAGE/ValheimPatcher/"
OUT="$SCRIPT_DIR/dist/ValheimPatcher-win-x64.zip"
rm -f "$OUT"
(cd "$STAGE" && zip -r -X -9 -q "$OUT" ValheimPatcher)
echo "==> Done: $OUT"
unzip -l "$OUT"
