#!/bin/bash
# Steam launch option wrapper. In Steam -> Valheim -> Properties -> Launch Options:
#   "/Users/<you>/Library/Application Support/Steam/steamapps/common/Valheim/autopatch.sh" %command%
# Re-applies the patch if Steam replaced assembly_valheim.dll (update / file verification),
# then starts the game. The game is started even if patching fails.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${VALHEIM_PATCH_VERSION:-v9}"
LOG="$SCRIPT_DIR/autopatch.log"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
DOTNET="$DOTNET_ROOT/dotnet"
PATCHER_DLL="$SCRIPT_DIR/Patcher/bin/Release/net10.0/ValheimPatcher.dll"

notify() {
    osascript -e "display notification \"$1\" with title \"Valheim Patcher\"" >/dev/null 2>&1 || true
}

ensure_patch() {
    echo "=== $(date '+%Y-%m-%d %H:%M:%S') ensure $VERSION"
    if [ ! -f "$PATCHER_DLL" ] || [ "$SCRIPT_DIR/Patcher/Program.cs" -nt "$PATCHER_DLL" ]; then
        "$DOTNET" build "$SCRIPT_DIR/Patcher" -c Release -v q --nologo || return 1
    fi
    "$DOTNET" "$PATCHER_DLL" ensure "$VERSION"
    local rc=$?
    if [ "$rc" -eq 3 ]; then
        xattr -cr "$SCRIPT_DIR/valheim.app"
        codesign --force --deep --sign - "$SCRIPT_DIR/valheim.app" || return 1
    fi
    return "$rc"
}

# Keep the log short
[ -f "$LOG" ] && tail -n 400 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"

ensure_patch >> "$LOG" 2>&1
rc=$?
case $rc in
    0) ;;
    3) notify "Патч $VERSION применён заново" ;;
    *) notify "Ошибка патча, игра запущена без него. См. autopatch.log" ;;
esac

if [ "$#" -gt 0 ]; then
    # Steam on macOS passes the .app bundle itself as %command%, which can't be exec'd.
    # Run the bundle's binary directly so Steam keeps tracking this process (overlay, playtime).
    if [ -d "$1" ] && [[ "$1" == *.app ]]; then
        app="$1"; shift
        exe_name="$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$app/Contents/Info.plist" 2>/dev/null)"
        set -- "$app/Contents/MacOS/${exe_name:-Valheim}" "$@"
        # Universal binary: force the native slice even if this shell was spawned under Rosetta
        if [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; then
            set -- arch -arm64 "$@"
        fi
    fi
    echo "launch: $*" >> "$LOG"
    if [ -n "$AUTOPATCH_DRY_RUN" ]; then
        exit 0
    fi
    exec "$@"
fi
[ "$rc" -eq 3 ] && exit 0
exit "$rc"
