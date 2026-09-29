# ReFix v1.2.0 — Universal OnlineFix Alternative, Steam Emulator & LAN Multiplayer Fix

[![Release](https://img.shields.io/badge/version-v1.2.0-blue.svg)](https://github.com/Coronitaa/ReFix/releases/tag/v1.2)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%7C%20x86-lightgrey.svg)](https://github.com/Coronitaa/ReFix)
[![Engines](https://img.shields.io/badge/engines-Unity%20%7C%20Unreal%20%7C%20Godot%20%7C%20Native-green.svg)](https://github.com/Coronitaa/ReFix)
[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)
[![Keywords](https://img.shields.io/badge/keywords-onlinefix%20%7C%20steam--crack%20%7C%20goldberg-orange.svg)](https://github.com/Coronitaa/ReFix)

**ReFix** is a high-performance, modular C++ DLL proxy, runtime Steamworks emulator, universal DLC unlocker, and multiplayer fix suite for PC games. Built as an open-source alternative to **OnlineFix (online-fix.me)**, **Goldberg Emulator**, **CODEX**, and **SmartSteamEmu**, ReFix enables seamless online co-op / multiplayer via Steam (Spacewar 480) or 100% offline local area network (LAN) play without requiring the Steam client.

---

## 🔍 Steam Emulators & Multiplayer Fixes Comparison Matrix

| Feature | ReFix v1.2.0 | OnlineFix (online-fix.me) | Goldberg Emulator (gbe_fork) | CODEX Steam Emu | SmartSteamEmu (SSE) |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Open Source (Full C++ Source)** | ✅ **Yes (MIT)** | ❌ Closed / Obfuscated | ✅ Yes (GPL/MIT) | ❌ Closed Source | ❌ Closed / Discontinued |
| **Steam Online Matchmaking (Spacewar 480)** | ✅ **Yes** | ✅ Yes | ❌ No (LAN only) | ❌ No (Offline only) | ❌ No (LAN only) |
| **100% Offline LAN Play (No Steam Required)** | ✅ **Yes (gbe_fork)** | ❌ No | ✅ Yes | ⚠️ Offline solo | ✅ Yes (Legacy LAN) |
| **Universal DLC Auto-Unlocker** | ✅ **Yes (SmokeAPI/CreamAPI)** | ⚠️ Manual / Partial | ⚠️ Config file only | ⚠️ INI list only | ⚠️ INI list only |
| **Interactive GUI Executable Picker** | ✅ **Yes (`select_exe.ps1`)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Smart Multi-Engine Detection (Unity/Unreal/Godot/Native)** | ✅ **Yes (Automated)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Epic Online Services (EOS / Redbone) Proxy** | ✅ **Yes (Built-in)** | ⚠️ Custom patches | ❌ No | ❌ No | ❌ No |
| **Automatic Windows Defender Firewall Config** | ✅ **Yes** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Steam Non-Steam Shortcut Injector** | ✅ **Yes (`shortcuts.vdf`)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Clean Zero-Trace Uninstaller & Game Restorer** | ✅ **Yes (1-Click)** | ❌ No | ❌ No | ❌ No | ❌ No |

---

## 🌟 Key Features & Architecture

### 1. Dual Connectivity Modes
* **Mode 1 — ReFix Online via Steam (Spacewar 480):**
  * Hooks into official Steam client via AppID 480 masking.
  * Enables online matchmaking, Steam lobbies, P2P networking, and Steam Overlay (Shift+Tab) through official Steam infrastructure.
  * Injects real game AppID into lobby metadata for worldwide lobby filtering and cross-player matchmaking.
* **Mode 2 — Re:Goldberg LAN without Steam (Offline / gbe_fork):**
  * 100% autonomous local emulation powered by the modern [gbe_fork](https://github.com/Detanup01/gbe_fork) / [Goldberg Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) backend.
  * Zero Steam installation or client dependency required.
  * Local subnet UDP broadcast discovery (default port `47584`), custom broadcast IP lists, persistent machine-unique SteamID64 identities, and portable save directory (`saves/`).

### 2. Universal DLC Unlocker (BLUESTAR Engine)
* Powered by lightweight [SmokeAPI](https://github.com/acidicoala/SmokeAPI) & [CreamAPI](https://github.com/acidicoala/CreamAPI) hooks.
* **Three Unlock Modes:**
  * **Unlock ALL:** Universal auto-unlock with Steam Store catalog caching.
  * **Unlock NONE:** Locks all DLCs for base-game parity testing.
  * **Custom Selection:** Interactive Steam Store API querying with real-time DLC titles, comma-separated index selection, ranges (e.g. `1-5`), or direct AppID inputs.
* Automatic generation of `cream_api.ini` and `SmokeAPI.config.json`.
* Non-destructive: Creates and manages original backups (`steam_api64_o.dll` / `steam_api_o.dll`).

### 3. Smart Multi-Engine Detection & Executable Selection
* Automatically inspects target directories for engine signatures:
  * **Unity:** `*_Data/Managed/`, `UnityPlayer.dll`, [BepInEx](https://github.com/BepInEx/BepInEx) loader integration.
  * **Unreal Engine 4/5:** `Binaries/Win64/`, `EOSSDK-Win64-Shipping.dll`, `RedboneEOS.dll`.
  * **Godot 3/4:** `*.pck` project packages, GodotSteam / SteamMultiplayerPeer.
  * **Native C/C++:** Custom engines (e.g. *Don't Starve Together*, Source engine titles).
* **Smart Binary Scoring:** Automatically filters out dedicated servers, crash handlers, nullrenderers, and helper tools (`*dedicated*`, `*nullrenderer*`, `*server*`), prioritizing main client 64-bit binaries.
* **Interactive Executable Picker:** Allows confirming the detected executable, choosing via native Windows File Explorer GUI dialog (`select_exe.ps1`), or entering custom paths.

### 4. Automatic Network & Firewall Configuration
* Embedded PowerShell and UAC helpers automatically configure Windows Defender Firewall rules for TCP/UDP game traffic and UDP LAN discovery ports.
* Generates a portable `Configure_LAN_Firewall.bat` helper in game root for plug-and-play USB / flash drive portability.

### 5. Steam Non-Steam Game Shortcut Auto-Installer
* Seamlessly injects the patched game into Steam's binary `shortcuts.vdf` for all local Steam user profiles.
* Reloads Steam automatically to enable Steam Overlay, Steam Input controller mapping, and Remote Play.

### 6. Universal Uninstaller & Restorer
* One-click rollback (`Uninstall_ReFix.bat`) that restores original backup DLLs (`.orig`, `_valve.dll`, `_o.dll`), restores SteamStub protected exes, and cleans up proxies, emulators, and temp caches without leaving residue.

---

## 📂 Project Structure

```
ReFix/
├── AutoDeploy.bat                      # Main Universal AutoDeploy batch tool (v1.2.0)
├── DLC_Unlocker.bat                    # Universal DLC Unlocker tool (v1.2.0)
├── Uninstall_ReFix.bat                 # Zero-trace uninstaller & game restorer
├── build.bat                           # MSVC build script for C++ proxies
├── deploy.bat                          # Automated packaging & deployment script
├── ReFix.ini                           # Unified configuration template
├── README.md                           # Comprehensive documentation & SEO guide
├── bin/                                # Deployment binaries & helper modules
│   ├── steam_api64.dll                 # ReFix Steam proxy DLL (x64)
│   ├── winmm.dll                       # ReFix winmm startup proxy loader
│   ├── EOSSDK-Win64-Shipping.dll       # EOS authentication proxy
│   ├── RedboneEOS.dll                  # Redpoint EOS bridge proxy
│   ├── ReFixSync.dll                   # Synchronization helper
│   ├── detect_game.ps1                 # Engine & executable analyzer
│   ├── deploy_helper.ps1               # Deployment engine & config synchronizer
│   ├── select_dlcs.ps1                 # Steam Store API DLC catalog scraper
│   ├── dlc_unlocker.ps1                # SmokeAPI/CreamAPI deployment manager
│   ├── select_folder.ps1               # GUI folder selection dialog
│   ├── select_exe.ps1                  # GUI executable selection dialog
│   ├── apply_firewall.ps1              # Windows Firewall rule automator
│   ├── add_steam_shortcut.ps1          # Steam shortcuts.vdf binary injector
│   ├── Install_ReFix_Steam_Shortcut.bat # Steam shortcut batch helper
│   ├── goldberg/                       # Goldberg emulator binaries & tools
│   ├── bepinex/                        # BepInEx runtime loader
│   └── tools/                          # SmokeAPI & Steamless unpacking utilities
└── src/                                # C++ Proxy source code
    ├── winmm_proxy.cpp                 # winmm.dll loader & hooking entry point
    ├── steam_proxy.cpp                 # steam_api64.dll export proxy & wrapper
    ├── eos_proxy.cpp                   # EOSSDK authentication & session emulator
    ├── unreal_detect.cpp               # Unreal Engine subsystem hooks
    ├── unreal_steam_emu.cpp            # Unreal Engine Steam adapter
    ├── upnp_firewall.cpp               # UPnP & firewall automation routines
    ├── server_browser_gui.cpp          # In-game ImGui server browser
    ├── include/                        # Steamworks SDK headers
    └── minhook/                        # MinHook hooking library
```

---

## 🚀 Quick Start Guide

### For Players (Deployment Package)

1. Download the latest **`ReFix_Release_v1.2.zip`** from [Releases](https://github.com/Coronitaa/ReFix/releases/tag/v1.2).
2. Extract the archive to any folder (or directly to a USB flash drive).
3. Run **`AutoDeploy.bat`**:
   - Choose your target game directory using the GUI dialog or enter the path.
   - Confirm or choose the executable (`.exe`).
   - Select your connectivity mode:
     - `[1] ReFix Online via Steam`: Online multiplayer with friends via Steam.
     - `[2] Re:Goldberg LAN without Steam`: Local LAN multiplayer without Steam running.
   - Choose your DLC preference (`[1] All`, `[2] None`, `[3] Custom`).
   - Launch your game and play!

### For DLC Unlocking Only

1. Run **`DLC_Unlocker.bat`**.
2. Select your game folder.
3. Choose `[1] Unlock ALL DLCs`, `[2] Choose specific DLCs`, or `[3] Unlock NO DLCs`.
4. The tool automatically configures SmokeAPI/CreamAPI and creates non-destructive backups.

### Restoring to Original Clean State

1. Run **`Uninstall_ReFix.bat`**.
2. Select your game folder.
3. All original files (`.orig`, `_valve.dll`, `_o.dll`, `.steamstub.exe`) are restored and all proxies/emulators are removed.

---

## ⚙️ Configuration Reference (`ReFix.ini`)

```ini
[Game]
GameName=GenericGame            ; Descriptive game title
EngineType=Auto                 ; Auto | Unity | Unreal | Godot | Native

[Online]
Mode=goldberg                   ; valve (Steam Online 480) | goldberg (LAN Offline)

[Steam]
MaskAppId=480                   ; Steam AppID used for masking (Spacewar 480)
RealAppId=550                   ; Real Steam AppID for DLCs and metadata
Language=english                ; Game language
BypassLicenseCheck=true         ; Allow running without Steam license ownership
DLCs=all                        ; all | none | comma-separated AppIDs

[Matchmaking]
EnableLobbyFilter=false         ; Filter lobbies by custom key
LobbyFilterKey=game_filter      ; Metadata key for game filtering
LobbyFilterValue=               ; Filter value (auto uses RealAppId if empty)
LobbyDistanceFilter=Worldwide   ; Close | Default | Far | Worldwide
MaxLobbyResults=50              ; Max lobbies returned

[ServerBrowser]
OverrideServerListAppId=false
ServerListAppId=480
Language=english

[Overlay]
EnableOverlay=true              ; Enable Steam Overlay hook
OverlayAppId=480

[EOS]
DeviceIdAuth=true               ; Emulate Epic Online Services DeviceID login

[User]
Name=Player                     ; Custom player name (or empty for auto-generated)
SteamId=                        ; Custom SteamID64 (or empty for auto-generated)

[Network]
ListenPort=47584                ; UDP port for LAN discovery
CustomBroadcasts=               ; Additional broadcast IPs (comma-separated)
PublicIP=                       ; Custom public IP for WAN relay
LocalIP=

[P2P]
EnableWAN=true                  ; Enable WAN NAT traversal
P2PPort=7777                    ; UDP P2P listening port
AllowRelay=true                 ; Allow relay servers if direct P2P fails
ForcePublicIPInLobby=true
```

---

## 🛠️ Building from Source

### Prerequisites
* **Windows 10 / 11 (64-bit)**
* **Visual Studio 2022** (Community, Professional, Enterprise, or Build Tools) with the **Desktop development with C++** workload enabled (includes MSVC v143 toolset + MASM `ml64.exe`).

### 1-Click Build
Open **Developer Command Prompt for VS 2022** (x64) or standard Windows Command Prompt / PowerShell and execute:
```cmd
cd ReFix
build.bat
```
`build.bat` automatically:
1. Locates the MSVC environment using `vswhere.exe` or standard Visual Studio installation directories.
2. Assembles x64 forwarding assembly tables (`winmm_fwd.asm`, `eos_fwd.asm`, `steam_fwd.asm`) via `ml64.exe`.
3. Compiles `winmm.dll`, `EOSSDK-Win64-Shipping.dll`, `RedboneEOS.dll`, and `steam_api64.dll` with high optimizations (`/O2 /EHsc /LD`).
4. Outputs the compiled proxies to `build/` and automatically synchronizes them to `bin/`.

### Packaging the Standalone Release
To package a clean deployment distribution:
```cmd
deploy.bat
```

---

## 📄 References, Credits & Related Projects

ReFix stands on the shoulders of giants. We express our deepest gratitude to the creators and maintainers of the following open-source projects and emulation tools:

* **[Goldberg Emulator (Original)](https://gitlab.com/Mr_Goldberg/goldberg_emulator)** by Mr_Goldberg — The foundation for open-source Steamworks emulation.
* **[gbe_fork (Goldberg Emulator Community Fork)](https://github.com/Detanup01/gbe_fork)** by Detanup01 & contributors — Modern Goldberg fork providing enhanced lobby broadcast, interface generation, and extended API support.
* **[SmokeAPI](https://github.com/acidicoala/SmokeAPI)** by acidicoala — Fast and modern universal Steam DLC unlocker backend.
* **[CreamAPI](https://github.com/acidicoala/CreamAPI)** by acidicoala — Legendary Steam DLC unlocker architecture and INI configuration standard.
* **[Mono.Cecil](https://github.com/jbevain/cecil)** by Jb Evain — Assembly inspection and IL patching library.
* **[MinHook](https://github.com/TsudaKageyu/minhook)** by Tsuda Kageyu — The Minimalistic x86/x64 API Hooking Library for Windows.
* **[Dear ImGui](https://github.com/ocornut/imgui)** by Omar Cornut — Bloat-free graphical user interface library for C++.
* **[Steamless](https://github.com/atom0s/Steamless)** by atom0s — DRM unpacker for SteamStub variants.

---

## 🏷️ Search Keywords & Tags (SEO)

`online-fix` `onlinefix` `online-fix-me` `steam-emulator` `steam-emu` `steam-crack` `goldberg-emulator` `goldberg-lan` `codex-steam-emulator` `smartsteamemu` `creamapi` `smokeapi` `dlc-unlocker` `steam-multiplayer-fix` `pirated-games-multiplayer` `spacewar-crack` `spacewar-480` `lan-multiplayer-fix` `coop-game-crack` `steam-api64-proxy` `unreal-engine-multiplayer-fix` `unity-multiplayer-fix` `godotsteam-fix` `steam-overlay-fix` `steam-keygen-bypass` `free-steam-multiplayer` `onlinefix-alternative` `game-crack-multiplayer`

---

## ⚖️ License

Distributed under the **Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)** License.

- **Modify & Collaborate**: You are free to adapt, remix, transform, build upon, and share the code.
- **Non-Commercial**: You may **not** use the material or software for commercial purposes or sell it.
- **ShareAlike**: Contributions and derivatives must be distributed under the same license terms.

See `LICENSE` for more information.
