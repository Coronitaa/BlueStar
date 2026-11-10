#!/usr/bin/env bash
# ReFix AutoDeploy for Linux (Steam Play / Proton / Wine).
#
# Installs ReFix Online or ReFix LAN into a Windows game folder, the same way AutoDeploy.bat does on Windows:
# the game's own Steam/EOS DLLs are backed up and replaced by the ReFix binaries for the chosen product.
#
#   bash AutoDeploy.sh                         interactive
#   bash AutoDeploy.sh <game folder|game.exe> [lan|offline|online] [appid] [refix|steam|none]
#     lan = ReFix Online + LAN, offline = ReFix Offline LAN (previous release), online = ReFix Online Legacy
#   bash AutoDeploy.sh --uninstall <game folder>
#
# The last argument (or REFIX_OVERLAY_MODE) picks the in-game overlay of ReFix LAN: the ReFix overlay
# (default), Steam's own, or none.
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

# ---------------------------------------------------------------- game mods
# Mods made for one game (refix_mods/catalog.tsv) go to <exe folder>/refix_mods, where ReFix loads them.
MODS_URL="${REFIX_MODS_URL:-https://raw.githubusercontent.com/Coronitaa/ReFix/main/refix_mods}"

fetch() {   # fetch <url> <file>
    if command -v curl >/dev/null 2>&1; then curl -fsSL --max-time 120 -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then wget -q -T 120 -O "$2" "$1"
    else return 1; fi
}

sha256_of() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }

install_mods() {
    local catalog="$ROOT/refix_mods/catalog.tsv" tmp="" id product exe mfile mtext file sum exe_path exe_dir dest src
    if [ ! -f "$catalog" ]; then
        tmp="$(mktemp)"
        fetch "$MODS_URL/catalog.tsv" "$tmp" || { rm -f "$tmp"; info "Mod catalog not reachable; no game mods installed"; return 0; }
        catalog="$tmp"
    fi
    while IFS=$'\t' read -r id product exe mfile mtext file sum; do
        sum="${sum%$'\r'}"
        case "$id" in ''|'#'*) continue ;; esac
        [ "$product" = any ] || [ "$product" = "$MODE" ] || continue
        exe_path="$(find "$GAME_DIR" -maxdepth 3 -type f -iname "$exe" -print -quit 2>/dev/null)"
        [ -n "$exe_path" ] || continue
        exe_dir="$(dirname "$exe_path")"
        if [ -n "$mfile" ] && ! grep -qaF "$mtext" "$exe_dir/$mfile" 2>/dev/null; then continue; fi
        dest="$exe_dir/refix_mods"
        mkdir -p "$dest"
        src="$ROOT/refix_mods/$file"
        if [ -f "$src" ]; then cp -f "$src" "$dest/$file.part"
        elif ! fetch "$MODS_URL/$file" "$dest/$file.part"; then
            rm -f "$dest/$file.part"; warn "Could not download the $id mod"; continue
        fi
        if [ "$(sha256_of "$dest/$file.part")" != "$sum" ]; then
            rm -f "$dest/$file.part"; warn "The $id package does not match the catalog; not installed"; continue
        fi
        mv -f "$dest/$file.part" "$dest/$file"
        grep -qxF "$file" "$dest/.refix_installed" 2>/dev/null || printf '%s\n' "$file" >> "$dest/.refix_installed"
        ok "Installed the $id mod in $dest"
        break
    done < "$catalog"
    [ -z "$tmp" ] || rm -f "$tmp"
    return 0
}

uninstall_mods() {
    local list d f
    while IFS= read -r list; do
        d="$(dirname "$list")"
        while IFS= read -r f; do f="${f%$'\r'}"; [ -n "$f" ] && rm -f "$d/$f"; done < "$list"
        rm -f "$list"
        rmdir "$d" 2>/dev/null || true
        ok "Removed the game mods ReFix installed in $d"
    done < <(find "$GAME_DIR" -type f -name .refix_installed 2>/dev/null)
    # Game files a mod changed keep their original as <file>.refix_orig.
    while IFS= read -r f; do
        mv -f "$f" "${f%.refix_orig}" && ok "Restored ${f%.refix_orig}"
    done < <(find "$GAME_DIR" -type f -name '*.refix_orig' 2>/dev/null)
}

# ---------------------------------------------------------------- uninstall
restore_one() {   # restore_one <dir> <backup name> <live name>
    local dir="$1" backup="$2" live="$3"
    [ -f "$dir/$backup" ] || return 0
    mv -f "$dir/$backup" "$dir/$live"
    ok "Restored $live in $dir"
}

# ReFix LAN profiles swap the game's save folder inside the Wine/Proton prefix; before uninstalling, the
# game gets this PC's own saves back (same rules as bin/restore_saves.ps1 on Windows). Nothing is deleted.
u16() { iconv -f UTF-16 -t UTF-8 "$1" 2>/dev/null | tr -d '\r'; }
sanitize() { printf '%s' "$1" | tr '<>:"/\\|?*' '_________'; }
move_entries() {   # move_entries <from> <to> [kept names...]
    local from="$1" to="$2" e name; shift 2
    [ -d "$from" ] || return 0
    mkdir -p "$to"
    for e in "$from"/* "$from"/.[!.]*; do
        [ -e "$e" ] || continue
        name="$(basename "$e")"
        case " $* " in *" $name "*) continue ;; esac
        mv "$e" "$to/"
    done
}

restore_saves() {
    command -v iconv >/dev/null 2>&1 || { warn "iconv not found: profile saves left as they are"; return 0; }
    local labels=() lives=() keeps=() info company product w proj name
    while IFS= read -r info; do
        company="$(sed -n 1p "$info" | tr -d '\r' | sed 's/^\xEF\xBB\xBF//')"
        product="$(sed -n 2p "$info" | tr -d '\r')"
        [ -n "$company" ] && [ -n "$product" ] || continue
        labels+=("Unity_$(sanitize "$company")_$(sanitize "$product")")
        lives+=("AppData/LocalLow/$company/$product")
        keeps+=("Player.log Player-prev.log output_log.txt Unity")
    done < <(find "$GAME_DIR" -maxdepth 4 -type f -path '*_Data/app.info' 2>/dev/null)
    while IFS= read -r w; do
        proj="$(dirname "$(dirname "$w")")"
        name="$(basename "$proj")"
        [ "$name" != Engine ] || continue
        [ -d "$(dirname "$proj")/Engine" ] || [ -d "$proj/Content" ] || continue
        labels+=("UE_$(sanitize "$name")")
        lives+=("AppData/Local/$name/Saved/SaveGames")
        keeps+=("")
    done < <(find "$GAME_DIR" -maxdepth 5 -type d -path '*/Binaries/Win64' 2>/dev/null)
    [ ${#labels[@]} -gt 0 ] || return 0

    local prefixes=() lib
    [ -n "${WINEPREFIX:-}" ] && prefixes+=("$WINEPREFIX")
    prefixes+=("$HOME/.wine")
    lib="$(cd "$GAME_DIR/../.." 2>/dev/null && pwd || true)"   # <library>/steamapps when the game is in steamapps/common
    for c in "$lib/compatdata" "$HOME/.steam/steam/steamapps/compatdata" "$HOME/.local/share/Steam/steamapps/compatdata"; do
        for pfx in "$c"/*/pfx; do [ -d "$pfx" ] && prefixes+=("$pfx"); done
    done

    local pfx owners refix user native prof text label owner live out in i changed clash e
    for pfx in "${prefixes[@]}"; do
        # AppData/Roaming/ReFix since 2.1, ReFix LAN before (not migrated while the game never ran with 2.1)
        for owners in "$pfx"/drive_c/users/*/AppData/Roaming/ReFix/saves.ini "$pfx"/drive_c/users/*/AppData/Roaming/"ReFix LAN"/saves.ini; do
            [ -f "$owners" ] || continue
            refix="$(dirname "$owners")"
            user="$(dirname "$(dirname "$(dirname "$refix")")")"
            text="$(u16 "$owners")"
            changed=0
            for i in "${!labels[@]}"; do
                label="${labels[$i]}"
                native=_pc
                if [ ! -d "$refix/Saves/_pc/$label" ] || [ -z "$(ls -A "$refix/Saves/_pc/$label" 2>/dev/null)" ]; then
                    for prof in "$refix"/Profiles/*/profile.ini; do
                        [ -f "$prof" ] && u16 "$prof" | grep -q '^NativeSaves=1' && native="$(basename "$(dirname "$prof")")"
                    done
                fi
                owner="$(printf '%s\n' "$text" | awk -v k="$label" 'index($0, k "=") == 1 { print substr($0, length(k) + 2) }')"
                [ -n "$owner" ] && [ "$owner" != "$native" ] || continue
                live="$user/${lives[$i]}"
                if [ "$owner" = _pc ]; then out="$refix/Saves/_pc/$label"; else out="$refix/Profiles/$owner/Saves/$label"; fi
                if [ "$native" = _pc ]; then in="$refix/Saves/_pc/$label"; else in="$refix/Profiles/$native/Saves/$label"; fi
                clash=0
                for e in "$live"/*; do [ -e "$e" ] && [ -e "$out/$(basename "$e")" ] && clash=1; done
                if [ "$clash" = 1 ]; then warn "$label: $out already has files with the same names; saves left as they are"; continue; fi
                # shellcheck disable=SC2086
                move_entries "$live" "$out" ${keeps[$i]}
                move_entries "$in" "$live"
                text="$(printf '%s\n' "$text" | awk -v k="$label" -v v="$native" 'index($0, k "=") == 1 { print k "=" v; next } { print }')"
                changed=1
                ok "$label: this PC's saves are back in $live; profile $owner keeps its own in $out"
            done
            [ "$changed" = 1 ] && printf '%s\n' "$text" | sed 's/$/\r/' | iconv -f UTF-8 -t UTF-16 > "$owners"
        done
    done
    return 0
}

uninstall() {
    info "Restoring the game's original files in $GAME_DIR"
    restore_saves
    uninstall_mods
    while IFS= read -r d; do
        restore_one "$d" steam_api64_original.dll steam_api64.dll
        restore_one "$d" steam_api64_valve.dll steam_api64.dll
        restore_one "$d" steam_api64_o.dll steam_api64.dll
        rm -f "$d/steam_api64_valve.dll"
    done < <({ dirs_with steam_api64_original.dll; dirs_with steam_api64_valve.dll; dirs_with steam_api64_o.dll; } | sort -u)
    while IFS= read -r d; do
        restore_one "$d" steam_api_original.dll steam_api.dll
        restore_one "$d" steam_api_valve.dll steam_api.dll
        restore_one "$d" steam_api_o.dll steam_api.dll
        rm -f "$d/steam_api_o.dll"
    done < <({ dirs_with steam_api_original.dll; dirs_with steam_api_valve.dll; dirs_with steam_api_o.dll; } | sort -u)
    while IFS= read -r d; do
        restore_one "$d" EOSSDK_original.dll EOSSDK-Win64-Shipping.dll
        rm -f "$d/RedboneEOS.dll"
    done < <({ dirs_with EOSSDK_original.dll; dirs_with RedboneEOS.dll; } | sort -u)
    while IFS= read -r d; do restore_one "$d" winmm_o.dll winmm.dll; done < <(dirs_with winmm_o.dll)
    find "$GAME_DIR" -type f \( -name "steam_appid.txt" -o -name "ReFix.ini" -o -name "ReFix.log" -o -name "local_save.txt" -o -name "refix_steamlink64.dll" -o -name "refix_steamlink.dll" -o -name "refix_steamhost64.exe" -o -name "refix_steamhost.exe" -o -name "ReFixFusionShared.log" \) -delete 2>/dev/null || true
    # ReFix support tools (Photon Fusion Shared server): only the folder holding ReFix's own server, then its
    # ReFix parent if nothing else is left in it.
    while IFS= read -r f; do
        rm -rf "$(dirname "$f")"
        rmdir "$(dirname "$(dirname "$f")")" 2>/dev/null || true
    done < <(find "$GAME_DIR" -type f -name ReFixFusionShared.exe 2>/dev/null)
    ok "Done. ReFix files removed and backups restored."
    echo "    ReFix LAN profiles and their saves stay in the prefix's AppData/Roaming/ReFix (shared by every game)."
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
        # ReFix Online + LAN: Valve's DLL and the helper for the optional Steam online services (overlay switch).
        # Only products that ship the helper have them (not Offline LAN).
        if [ "$MODE" = lan ] && [ -f "$PRODUCT_BIN/refix_steamhost64.exe" ] && [ -f "$BIN/valve/steam_api64.dll" ]; then
            cp -f "$BIN/valve/steam_api64.dll" "$d/refix_steamlink64.dll"
            cp -f "$PRODUCT_BIN/refix_steamhost64.exe" "$d/refix_steamhost64.exe"
        fi
    done < <({ dirs_with steam_api64.dll; dirs_with steam_api64_original.dll; } | sort -u)
}

deploy_steam_x86() {
    local src="$PRODUCT_BIN/x86/steam_api.dll" d
    [ -f "$src" ] || die "Missing $src"
    while IFS= read -r d; do
        # Installs before 2.2.0 kept a second copy as steam_api_o.dll; the proxy now forwards to steam_api_original.dll.
        if [ -f "$d/steam_api_o.dll" ]; then
            if [ -f "$d/steam_api_original.dll" ]; then rm -f "$d/steam_api_o.dll"; else mv -f "$d/steam_api_o.dll" "$d/steam_api_original.dll"; fi
        fi
        backup_original "$d" steam_api.dll steam_api_original.dll
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
    # Free-to-play games keep their real AppID online: their backend checks a Steam ticket for it.
    local mask=480 store="$(mktemp)"
    if [ "$MODE" = online ] && [ "$APP_ID" != 480 ] &&
       fetch "https://store.steampowered.com/api/appdetails?appids=$APP_ID&filters=basic" "$store" &&
       grep -q '"is_free":true' "$store"; then
        mask="$APP_ID"
        echo "[INFO] AppID $APP_ID is free to play on Steam: ReFix Online will use it without masking."
    fi
    rm -f "$store"
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
MaskAppId = $mask
RealAppId = $APP_ID
Language = english
BypassLicenseCheck = true
DLCs = all

[User]
IdentityMode = auto
Name = ${USER_NAME}
AutoGenerateSteamId = true
SteamId =

[Overlay]
; Which overlay the game shows:
;   ReFix overlay -> Enabled = true,  BlockSteamOverlay = true
;   Steam overlay -> Enabled = false, BlockSteamOverlay = false  (only while the Steam client runs)
;   No overlay    -> Enabled = false, BlockSteamOverlay = true
; Enabled: ReFix overlay (Shift+Tab, or the Xbox / PS button of a controller): profiles, players on the
; LAN, invites and the game's web pages.
Enabled = $OV_ENABLED
; BlockSteamOverlay: keeps Steam's own overlay out of the game.
BlockSteamOverlay = $OV_BLOCK
; Short, quiet sounds when the ReFix overlay opens and closes, for invites and for controller navigation.
Sounds = true
; Each profile keeps its own Steam Cloud files and the game's known save folder (Unreal SaveGames, Unity LocalLow).
IsolateSaves = true
FirstRunDialog = false

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
        if [ "$MODE" = lan ]; then
            ini_set "$ini" Overlay Enabled "$OV_ENABLED"
            ini_set "$ini" Overlay BlockSteamOverlay "$OV_BLOCK"
        fi
        ok "Updated ReFix.ini in $EXE_DIR for ReFix $MODE"
    fi
    # The game's server list (dedicated / community servers) under its real AppID: as Spacewar it shows none.
    if [ "$APP_ID" != 480 ] && [ "$APP_ID" != "$mask" ]; then
        ini_set "$ini" ServerBrowser OverrideServerListAppId true
        ini_set "$ini" ServerBrowser ServerListAppId "$APP_ID"
    fi
    printf '%s\n' "$APP_ID" > "$EXE_DIR/steam_appid.txt"
    ok "steam_appid.txt = $APP_ID"
}

# ---------------------------------------------------------------- main
UNINSTALL=0
if [ "${1:-}" = "--uninstall" ]; then UNINSTALL=1; shift; fi

TARGET="${1:-}"
[ -n "$TARGET" ] || TARGET="$(ask_game_dir)"
if [ -z "$TARGET" ]; then
    warn "No folder selected or operation cancelled."
    exit 0
fi
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
    echo "  [1] ReFix Online + LAN          - local network; Steam friends optional from the overlay (recommended)"
    echo "  [2] ReFix Offline LAN           - the LAN version of the previous release, local network only"
    echo "  [3] ReFix Online Legacy via Steam - uses the Steam client (start the game from Steam)"
    read -r -p "Mode [1/2/3] (default 1): " choice
    case "${choice:-1}" in 1) MODE=lan ;; 2) MODE=offline ;; 3) MODE=online ;; *) die "Choose 1, 2 or 3" ;; esac
fi
case "$MODE" in online|lan|offline) ;; *) die "Mode must be 'lan', 'offline' or 'online'" ;; esac
# Offline LAN installs exactly like ReFix LAN, from its own folder (bin/lan_offline).
PRODUCT_DIR="$MODE"
if [ "$MODE" = offline ]; then MODE=lan; PRODUCT_DIR=lan_offline; fi
PRODUCT_BIN="$BIN/$PRODUCT_DIR"
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

OVERLAY="${4:-${REFIX_OVERLAY_MODE:-}}"
if [ "$MODE" = lan ] && [ -z "$OVERLAY" ] && [ -t 0 ]; then
    echo
    echo "  In-game overlay:"
    echo "  [1] ReFix overlay (Shift+Tab, or the Xbox / PS button of a controller)"
    echo "  [2] Steam overlay (only while the Steam client runs)"
    echo "  [3] No overlay"
    read -r -p "Overlay [1-3] (Enter = 1): " OVERLAY
fi
case "${OVERLAY,,}" in 2|steam) OVERLAY=steam ;; 3|none|off) OVERLAY=none ;; *) OVERLAY=refix ;; esac
if [ "$OVERLAY" = refix ]; then OV_ENABLED=true; else OV_ENABLED=false; fi
if [ "$OVERLAY" = steam ]; then OV_BLOCK=false; else OV_BLOCK=true; fi

if [ -z "$(dirs_with steam_api64.dll; dirs_with steam_api.dll; dirs_with steam_api64_original.dll; dirs_with steam_api_original.dll)" ]; then
    die "No steam_api64.dll or steam_api.dll found under $GAME_DIR"
fi

info "Game:   $GAME_DIR"
info "Mode:   ReFix $MODE (binaries from $PRODUCT_BIN)"
info "AppID:  $APP_ID"
[ "$MODE" = lan ] && info "Overlay: $OVERLAY"
deploy_steam_x64
deploy_steam_x86
deploy_eos
clean_online_winmm
write_ini
install_mods
# ReFix Online + LAN: the Photon Fusion Shared server it starts next to the game (shipped with the Online product)
if [ "$PRODUCT_DIR" = lan ] && [ -d "$BIN/online/ReFix" ]; then
    cp -rf "$BIN/online/ReFix" "$EXE_DIR/" && ok "Deployed ReFix support tools (Fusion Shared server) to $EXE_DIR/ReFix"
fi

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
