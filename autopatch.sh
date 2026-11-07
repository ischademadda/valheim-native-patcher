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
    # Rebuild when any patcher or embedded helper (profiler, mist) source is newer than the build
    if [ ! -f "$PATCHER_DLL" ] || [ -n "$(find "$SCRIPT_DIR/Patcher" "$SCRIPT_DIR/NativeProfiler" "$SCRIPT_DIR/NativeMist" \( -name '*.cs' -o -name '*.csproj' \) -not -path '*/obj/*' -not -path '*/bin/*' -newer "$PATCHER_DLL" | head -n 1)" ]; then
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
    # Steam on macOS passes the .app bundle itself as %command%. Launch it through LaunchServices
    # ("open"), exactly like Steam does without launch options: exec'ing the binary directly skips
    # the normal app launch, so macOS Game Mode / foreground priority may not apply.
    # -W keeps this script alive until the game quits, so Steam still tracks the session.
    if [ -d "$1" ] && [[ "$1" == *.app ]]; then
        app="$1"; shift
        # LaunchServices does not inherit our environment: forward Steam's variables explicitly,
        # otherwise the game may think it wasn't started by Steam and relaunch itself through it.
        env_args=()
        for name in $(compgen -e); do
            case "$name" in
                Steam*|STEAM*|SDL_*|DYLD_INSERT_LIBRARIES) env_args+=(--env "$name=${!name}") ;;
            esac
        done
        [ -z "$SteamAppId" ] && env_args+=(--env "SteamAppId=892970")
        [ -z "$SteamGameId" ] && env_args+=(--env "SteamGameId=892970")
        set -- open -W -a "$app" "${env_args[@]}" --args "$@"
    fi
    echo "launch: $*" >> "$LOG"
    if [ -n "$AUTOPATCH_DRY_RUN" ]; then
        exit 0
    fi
    exec "$@"
fi
[ "$rc" -eq 3 ] && exit 0
exit "$rc"
