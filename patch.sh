#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"

echo "==> Building Patcher..."
dotnet build "$SCRIPT_DIR/Patcher" -c Release -v q --nologo

echo "==> Running Patcher..."
dotnet "$SCRIPT_DIR/Patcher/bin/Release/net10.0/ValheimPatcher.dll" "$@"

echo "==> Re-signing Valheim.app for macOS Gatekeeper..."
xattr -cr "$SCRIPT_DIR/valheim.app"
codesign --force --deep --sign - "$SCRIPT_DIR/valheim.app"

echo "==> Done! You can now launch Valheim."
