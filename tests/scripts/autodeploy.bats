#!/usr/bin/env bats
# AutoDeploy.sh / Uninstall_ReFix.sh against fake game trees. Runs on Linux CI and under Git Bash.
# The script is ReFix's own (updates overwrite it): these tests pin the contract BlueStar relies on.

SUITE="$BATS_TEST_DIRNAME/../../src/BlueStar.App/tools/ReFix_deploy"

setup() {
    GAME="$BATS_TEST_TMPDIR/game"
    mkdir -p "$GAME"
    export REFIX_USERNAME="tester"
    export REFIX_MODS_URL="http://127.0.0.1:9/none" # never reach the network for mods
}

valve_dll() { mkdir -p "$(dirname "$1")"; printf 'valve original' > "$1"; }

deploy() { run bash "$SUITE/AutoDeploy.sh" "$GAME" "$@" </dev/null; }

@test "dies without any steam_api dll" {
    touch "$GAME/Game.exe"
    deploy lan 480 none
    [ "$status" -ne 0 ]
    [[ "$output" == *"No steam_api64.dll or steam_api.dll"* ]]
}

@test "rejects an unknown mode and a non-numeric appid" {
    valve_dll "$GAME/steam_api64.dll"
    deploy bogus 480 none
    [ "$status" -ne 0 ]
    deploy lan abc none
    [ "$status" -ne 0 ]
}

@test "unity: backs up the original and writes ini + appid in the given folder" {
    valve_dll "$GAME/Game_Data/Plugins/x86_64/steam_api64.dll"
    touch "$GAME/Game.exe"
    deploy lan 480 none
    [ "$status" -eq 0 ]
    [ "$(cat "$GAME/Game_Data/Plugins/x86_64/steam_api64_original.dll")" = "valve original" ]
    grep -qa ReFix "$GAME/Game_Data/Plugins/x86_64/steam_api64.dll"
    [ "$(cat "$GAME/steam_appid.txt")" = "480" ]
    grep -q "Name = tester" "$GAME/ReFix.ini"
    grep -q "Mode = goldberg" "$GAME/ReFix.ini"
}

@test "unreal: ini lands in the game root, not Binaries/Win64 (BlueStar moves it in C#)" {
    valve_dll "$GAME/Engine/Binaries/ThirdParty/Steamworks/Steamv157/Win64/steam_api64.dll"
    mkdir -p "$GAME/Game/Binaries/Win64" && touch "$GAME/Game/Binaries/Win64/Game-Win64-Shipping.exe"
    deploy lan 480 none
    [ "$status" -eq 0 ]
    [ -f "$GAME/ReFix.ini" ]
    [ ! -f "$GAME/Game/Binaries/Win64/ReFix.ini" ]
}

@test "x86: replaces steam_api.dll too" {
    valve_dll "$GAME/steam_api.dll"
    touch "$GAME/Game.exe"
    deploy lan 480 none
    [ "$status" -eq 0 ]
    [ "$(cat "$GAME/steam_api_original.dll")" = "valve original" ]
    grep -qa ReFix "$GAME/steam_api.dll"
}

@test "redeploy keeps the real original and the player's ini settings" {
    valve_dll "$GAME/steam_api64.dll"
    deploy lan 480 none
    [ "$status" -eq 0 ]
    printf '\n[Custom]\nKeep = yes\n' >> "$GAME/ReFix.ini"
    deploy offline 480 none
    [ "$status" -eq 0 ]
    [ "$(cat "$GAME/steam_api64_original.dll")" = "valve original" ]
    grep -q "Keep = yes" "$GAME/ReFix.ini"
}

@test "uninstall restores the original dll and removes ReFix files" {
    valve_dll "$GAME/steam_api64.dll"
    deploy lan 480 none
    [ "$status" -eq 0 ]
    run bash "$SUITE/Uninstall_ReFix.sh" "$GAME" </dev/null
    [ "$status" -eq 0 ]
    [ "$(cat "$GAME/steam_api64.dll")" = "valve original" ]
    [ ! -f "$GAME/ReFix.ini" ]
    [ ! -f "$GAME/steam_appid.txt" ]
}
