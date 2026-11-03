#!/usr/bin/env bash
# ReFix AutoDeploy for Linux (Steam Play / Proton / Wine).
#
# Installs ReFix Online or ReFix LAN into a Windows game folder, the same way AutoDeploy.bat does on Windows:
# the game's own Steam/EOS DLLs are backed up and replaced by the ReFix binaries for the chosen product.
#
#   bash AutoDeploy.sh                         interactive
#   bash AutoDeploy.sh <game folder|game.exe> [lan|online] [appid]
#   bash AutoDeploy.sh --uninstall <game folder>
#
# ReFix Online needs the Steam client running (launch the game from Steam with Proton).
# ReFix LAN needs no Steam: players on the same network find each other.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$ROOT/bin"

info() { printf '\033[36m[*]\033[0m %s\n' "$*"; }
ok()   { printf '\033[32m[OK]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

# A DLL written by ReFix carries its name; the game's own Valve/Epic DLLs never do.
is_refix() { [ -f "$1" ] && grep -qa "ReFix" "$1"; }

# Case-insensitive search under the game folder, one directory per line, without duplicates.
dirs_with() { find "$GAME_DIR" -type f -iname "$1" -printf '%h\n' 2>/dev/null | sort -u; }

# Picks a folder with zenity/kdialog when there is a desktop, else asks on the terminal.
ask_game_dir() {
    local picked=""
    if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
        if command -v zenity >/dev/null 2>&1; then
            picked="$(zenity --file-selection --directory --title="ReFix: select the game folder" 2>/dev/null || true)"
        elif command -v kdialog >/dev/null 2>&1; then
            picked="$(kdialog --getexistingdirectory "$HOME" --title "ReFix: select the game folder" 2>/dev/null || true)"
        fi
    fi
    if [ -z "$picked" ]; then
        read -r -p "Game folder (or the game's .exe): " picked
    fi
    printf '%s' "$picked"
}

# ---------------------------------------------------------------- uninstall
restore_one() {   # restore_one <dir> <backup name> <live name>
    local dir="$1" backup="$2" live="$3"
    [ -f "$dir/$backup" ] || return 0
    mv -f "$dir/$backup" "$dir/$live"
    ok "Restored $live in $dir"
}

uninstall() {
    info "Restoring the game's original files in $GAME_DIR"
    while IFS= read -r d; do restore_one "$d" steam_api64_original.dll steam_api64.dll; rm -f "$d/steam_api64_valve.dll"; done < <(dirs_with steam_api64_original.dll)
    while IFS= read -r d; do restore_one "$d" steam_api_original.dll steam_api.dll; rm -f "$d/steam_api_o.dll"; done < <(dirs_with steam_api_original.dll)
    while IFS= read -r d; do restore_one "$d" EOSSDK_original.dll EOSSDK-Win64-Shipping.dll; done < <(dirs_with EOSSDK_original.dll)
    while IFS= read -r d; do restore_one "$d" winmm_o.dll winmm.dll; done < <(dirs_with winmm_o.dll)
    ok "Done. ReFix.ini and saves were left in place."
}

# ---------------------------------------------------------------- deploy steps
# Keeps the game's genuine DLL once as <backup>; a ReFix file is never taken for the original.
backup_original() {   # backup_original <dir> <live name> <backup name>
    local live="$1/$2" backup="$1/$3"
    if [ -f "$live" ] && [ ! -f "$backup" ] && ! is_refix "$live"; then
        cp -f "$live" "$backup"
        ok "Backed up original $2 -> $3 in $1"
    fi
}

deploy_steam_x64() {
    local src="$PRODUCT_BIN/steam_api64.dll" d
    [ -f "$src" ] || die "Missing $src"
    while IFS= read -r d; do
        backup_original "$d" steam_api64.dll steam_api64_original.dll
        # ReFix Online forwards to a genuine Valve DLL: the game's own, else the stock one shipped with ReFix.
        if [ "$MODE" = online ] && [ ! -f "$d/steam_api64_original.dll" ] && [ ! -f "$d/steam_api64_valve.dll" ]; then
            if [ -f "$BIN/valve/steam_api64.dll" ]; then
                cp -f "$BIN/valve/steam_api64.dll" "$d/steam_api64_valve.dll"
                ok "Added stock Valve steam_api64.dll -> steam_api64_valve.dll in $d"
            else
                warn "No genuine Valve steam_api64.dll for $d; ReFix Online may fail to reach Steam"
            fi
        fi
        cp -f "$src" "$d/steam_api64.dll"
        ok "Deployed ReFix $MODE steam_api64.dll to $d"
    done < <({ dirs_with steam_api64.dll; dirs_with steam_api64_original.dll; } | sort -u)
}

deploy_steam_x86() {
    local src="$PRODUCT_BIN/x86/steam_api.dll" d
    [ -f "$src" ] || die "Missing $src"
    while IFS= read -r d; do
        backup_original "$d" steam_api.dll steam_api_original.dll
        if [ "$MODE" = online ] && [ -f "$d/steam_api_original.dll" ]; then
            cp -f "$d/steam_api_original.dll" "$d/steam_api_o.dll"   # Online x86 forwards to steam_api_o.dll
        fi
        cp -f "$src" "$d/steam_api.dll"
        ok "Deployed ReFix $MODE x86 steam_api.dll to $d"
    done < <({ dirs_with steam_api.dll; dirs_with steam_api_original.dll; } | sort -u)
}

deploy_eos() {
    local src="$PRODUCT_BIN/EOSSDK-Win64-Shipping.dll" redbone="$PRODUCT_BIN/RedboneEOS.dll" d
    [ -f "$src" ] || return 0
    [ -f "$redbone" ] || redbone="$src"
    while IFS= read -r d; do
        if [ -f "$d/EOSSDK-Win64-Shipping.dll" ] && [ ! -f "$d/EOSSDK_original.dll" ] && ! is_refix "$d/EOSSDK-Win64-Shipping.dll"; then
            mv -f "$d/EOSSDK-Win64-Shipping.dll" "$d/EOSSDK_original.dll"
            ok "Backed up original EOSSDK -> EOSSDK_original.dll in $d"
        fi
        cp -f "$src" "$d/EOSSDK-Win64-Shipping.dll"
        ok "Deployed ReFix EOSSDK-Win64-Shipping.dll to $d"
        if [ -f "$d/RedboneEOS.dll" ]; then cp -f "$redbone" "$d/RedboneEOS.dll"; ok "Deployed RedboneEOS.dll to $d"; fi
    done < <({ dirs_with EOSSDK-Win64-Shipping.dll; dirs_with EOSSDK_original.dll; } | sort -u)
}

# ReFix LAN has no winmm loader: drop a ReFix Online winmm.dll left by an earlier Online deploy.
clean_online_winmm() {
    local w="$EXE_DIR/winmm.dll"
    [ "$MODE" = lan ] && is_refix "$w" || return 0
    rm -f "$w"
    [ -f "$EXE_DIR/winmm_o.dll" ] && mv -f "$EXE_DIR/winmm_o.dll" "$w"
    ok "Removed the ReFix Online winmm.dll from $EXE_DIR"
}

uses_photon() {
    find "$GAME_DIR" -type f \( -iname 'Photon3Unity3D.dll' -o -iname 'PhotonRealtime.dll' -o -iname 'Fusion.Runtime.dll' \
        -o -iname 'PhotonUnityNetworking.dll' \) -print -quit 2>/dev/null | grep -q . && return 0
    local meta
    meta="$(find "$GAME_DIR" -type f -name 'global-metadata.dat' -print -quit 2>/dev/null)"
    [ -n "$meta" ] && grep -qaE 'ExitGames\.Client\.Photon|Photon\.Realtime|Fusion\.NetworkRunner' "$meta"
}

# Sets key=value inside [section] of an INI file, adding the section or key when missing.
ini_set() {   # ini_set <file> <section> <key> <value>
    local tmp; tmp="$(mktemp)"
    awk -v sec="[$2]" -v key="$3" -v val="$4" '
        function flush() { if (insec && !done) { print key " = " val; done = 1 } }
        /^\[.*\][[:space:]]*$/ { flush(); insec = ($0 == sec); print; next }
        insec && $0 ~ "^[[:space:]]*" key "[[:space:]]*=" { print key " = " val; done = 1; next }
        { print }
        END { flush(); if (!done) { print ""; print sec; print key " = " val } }' "$1" > "$tmp"
    mv -f "$tmp" "$1"
}

write_ini() {
    local ini="$EXE_DIR/ReFix.ini" photon eos emu
    if [ "$MODE" = lan ]; then emu=goldberg; eos=emulated; else emu=valve; eos=passthrough; fi
    if uses_photon; then photon=ReFixCloud; else photon=OfficialPhoton; fi
    if [ ! -f "$ini" ]; then
        cat > "$ini" <<EOF
; ReFix configuration (written by AutoDeploy.sh). See README.txt for every option.
[Game]
GameName = $GAME_NAME

[Network]
Mode = auto
DiscoveryPort = 47584
DiscoveryGroup = 239.255.71.84
CustomBroadcasts =

[Photon]
Backend = $photon

[Online]
Mode = $emu

[Steam]
MaskAppId = 480
RealAppId = $APP_ID
Language = english
BypassLicenseCheck = true
DLCs = all

[User]
IdentityMode = auto
Name = ${USER_NAME}
AutoGenerateSteamId = true
SteamId =

[EOS]
Mode = $eos

[Debug]
EnableLog = false
EOF
        ok "Wrote ReFix.ini in $EXE_DIR"
    else
        # Keep the player's own settings; only the product-dependent keys follow the chosen mode.
        ini_set "$ini" Online Mode "$emu"
        ini_set "$ini" EOS Mode "$eos"
        ini_set "$ini" Steam RealAppId "$APP_ID"
        ok "Updated ReFix.ini in $EXE_DIR for ReFix $MODE"
    fi
    printf '%s\n' "$APP_ID" > "$EXE_DIR/steam_appid.txt"
    ok "steam_appid.txt = $APP_ID"
}

# ---------------------------------------------------------------- main
UNINSTALL=0
if [ "${1:-}" = "--uninstall" ]; then UNINSTALL=1; shift; fi

TARGET="${1:-}"
[ -n "$TARGET" ] || TARGET="$(ask_game_dir)"
TARGET="${TARGET%/}"
[ -e "$TARGET" ] || die "Not found: $TARGET"
if [ -f "$TARGET" ]; then
    case "${TARGET,,}" in *.exe) ;; *) die "Pass the game folder or its .exe: $TARGET" ;; esac
    EXE_DIR="$(cd "$(dirname "$TARGET")" && pwd)"
    GAME_DIR="$EXE_DIR"
else
    GAME_DIR="$(cd "$TARGET" && pwd)"
    EXE_DIR="$GAME_DIR"
fi
GAME_NAME="$(basename "$GAME_DIR")"

if [ "$UNINSTALL" = 1 ]; then uninstall; exit 0; fi

MODE="${2:-}"
if [ -z "$MODE" ]; then
    echo
    echo "  [1] ReFix Online via Steam  - uses the Steam client (start the game from Steam)"
    echo "  [2] ReFix LAN without Steam - plays on the local network, Steam not needed"
    read -r -p "Mode [1/2]: " choice
    case "$choice" in 1) MODE=online ;; 2) MODE=lan ;; *) die "Choose 1 or 2" ;; esac
fi
case "$MODE" in online|lan) ;; *) die "Mode must be 'online' or 'lan'" ;; esac
PRODUCT_BIN="$BIN/$MODE"
[ -d "$PRODUCT_BIN" ] || die "ReFix $MODE binaries not found in $PRODUCT_BIN"

APP_ID="${3:-}"
if [ -z "$APP_ID" ]; then
    existing="$(find "$GAME_DIR" -maxdepth 3 -type f -name steam_appid.txt -print -quit 2>/dev/null)"
    [ -n "$existing" ] && APP_ID="$(tr -dc '0-9' < "$existing")"
fi
if [ -z "$APP_ID" ]; then
    read -r -p "Steam AppID of the game (Enter = 480): " APP_ID
fi
APP_ID="${APP_ID:-480}"
case "$APP_ID" in *[!0-9]*) die "AppID must be a number: $APP_ID" ;; esac
USER_NAME="${REFIX_USERNAME:-${USER:-Player}}"

if [ -z "$(dirs_with steam_api64.dll; dirs_with steam_api.dll; dirs_with steam_api64_original.dll; dirs_with steam_api_original.dll)" ]; then
    die "No steam_api64.dll or steam_api.dll found under $GAME_DIR"
fi

info "Game:   $GAME_DIR"
info "Mode:   ReFix $MODE (binaries from $PRODUCT_BIN)"
info "AppID:  $APP_ID"
deploy_steam_x64
deploy_steam_x86
deploy_eos
clean_online_winmm
write_ini

echo
ok "ReFix $MODE is installed in $GAME_NAME."
if [ "$MODE" = online ]; then
    echo "    Start the game from Steam (Proton). Steam must be running."
else
    echo "    Start the game's .exe with Proton or Wine, or add it to Steam as a non-Steam game."
    echo "    Every PC and every instance gets its own identity; players on the same network find each other."
    echo "    Allow UDP 47584 and the game's ports in your firewall if players do not see each other."
fi
echo "    Undo with: bash AutoDeploy.sh --uninstall \"$GAME_DIR\""
