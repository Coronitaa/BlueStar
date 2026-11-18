# ReFix 2.5.3

## Older game builds
- **Steam networking sockets on 2018-era SDKs.** Games that ask for `SteamNetworkingSocketsSerialized001/002/003` no longer get the wrong interface and crash at startup. 001 returns NULL; 002 and 003+ get stubs with their exact x86 signatures.
- **Game Coordinator hello** now reports a price sheet version, so clients that build their store UI from it no longer crash in the main menu.
- `bin/lan_offline/x86/steam_api.dll` is rebuilt with both fixes.

## Mods
- A mod hook no longer assumes a fixed vtable slot. The slot is found from the code itself, so the same mod works across game builds that moved the method.
- The overlay's LAN server dialog reads each build's modes, player limits and defaults from the game's own files, with localized names. Host settings survive the map load.
- The Game Coordinator only sends the items each build defines, so older clients don't show newer items without icons.
- **Pick which catalog mods a deploy installs** with `REFIX_GAME_MODS`: unset keeps the old behaviour (the first catalog mod that matches the game), `none` installs none, and a comma-separated list installs each listed mod that matches. Launchers such as BlueStar set it.

## Portable installs
- **AutoDeploy asks whether to include portable runtime libraries** (default yes): the DirectX runtime DLLs the game needs (d3dx9, XAudio2, XInput...), taken from the redistributable the game ships and placed next to the files that load them. Unattended deploys pass `REFIX_PORTABLE_RUNTIME=Y/N`.
- **XAudio2 2.7 without the DirectX runtime.** When the class is not registered, ReFix creates it from the game's own `XAudio2_7.dll`.

## Steam link
- x86 games get a current Valve `steam_api.dll` (`bin/valve/steam_api.dll`, deployed as `refix_steamlink.dll`), since old games ship SDKs without the flat API the Steam helper needs.
- `refix_steamhost` no longer waits forever when the Steam client leaves `ActiveUser` at 0 while signed in.
- From the second instance on, Source engine games start in a borderless window unless a window mode was passed, so exclusive fullscreen in one instance no longer blacks out the others.
- Proton: the Steam environment is preserved and the helper logs more diagnostics. Thanks @Syntist (#32).
- Wine: overlay input and font rendering fixes. Thanks @Syntist (#31).

## Deploy
- `IdentityMode = fixed` is honored, and a first deploy takes the identity from the package's `bin/ReFix.ini` (#30).

## Linux
- `AutoDeploy.sh` and `Uninstall_ReFix.sh` ship in the same zip as before. #31 and #32 were not tested on a Linux machine.
