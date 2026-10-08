#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"

echo "==> Building Patcher..."
dotnet build "$SCRIPT_DIR/Patcher" -c Release -v q --nologo

# Determine if this command modifies files
IS_READ_ONLY=0
for arg in "$@"; do
    case "$arg" in
        --status|status|--verify|verify|--help|-h|help)
            IS_READ_ONLY=1
            ;;
    esac
done

echo "==> Running Patcher..."
dotnet "$SCRIPT_DIR/Patcher/bin/Release/net10.0/ValheimPatcher.dll" "$@"

if [ "$IS_READ_ONLY" -eq 0 ]; then
    echo "==> Re-signing Valheim.app for macOS Gatekeeper..."
    xattr -cr "$SCRIPT_DIR/valheim.app"
    codesign --force --deep --sign - "$SCRIPT_DIR/valheim.app"
    echo "==> Done! You can now launch Valheim."
fi
