# ReFix v1.3.2 — Universal Multiplayer Fix, Steam & EOS Emulator, and DLC Unlocker

[![Release](https://img.shields.io/badge/version-v1.3.2-blue.svg)](https://github.com/Coronitaa/ReFix/releases/tag/v1.3.2)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%7C%20x86-lightgrey.svg)](https://github.com/Coronitaa/ReFix)
[![Engines](https://img.shields.io/badge/engines-Unity%20%7C%20Unreal%20%7C%20Godot%20%7C%20Native-green.svg)](https://github.com/Coronitaa/ReFix)
[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)
[![Signed: Corøna](https://img.shields.io/badge/signed-BlueStar%20Developers-blueviolet.svg)](https://github.com/Coronitaa/ReFix)

Hey everyone! Welcome to **ReFix** — your open-source, all-in-one C++ proxy suite designed to get multiplayer, online co-op, and DLCs working on PC games without the headaches. 

Whether you want to play online with friends over Steam (using Spacewar AppID 480 masking) or host a LAN party completely offline without even touching the Steam client, ReFix has you covered. It's built as a clean, transparent, and bloat-free alternative to closed-source solutions like OnlineFix (online-fix.me), Goldberg Emulator, and legacy scene emus.

---

## 🔍 How ReFix Compares

| Feature | ReFix v1.3.2 | OnlineFix (online-fix.me) | Goldberg Emulator (gbe_fork) | CODEX Steam Emu | SmartSteamEmu (SSE) |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Open Source (Full C++ Source)** | ✅ **Yes (CC BY-NC-SA)** | ❌ Closed / Obfuscated | ✅ Yes (GPL/MIT) | ❌ Closed Source | ❌ Closed / Abandoned |
| **Steam Online Matchmaking (Spacewar 480)** | ✅ **Yes** | ✅ Yes | ❌ No (LAN only) | ❌ No (Offline only) | ❌ No (LAN only) |
| **100% Offline LAN Play (No Steam Required)** | ✅ **Yes (gbe_fork)** | ❌ No | ✅ Yes | ⚠️ Offline solo | ✅ Yes (Legacy LAN) |
| **Universal DLC Auto-Unlocker** | ✅ **Yes (SmokeAPI/CreamAPI)** | ⚠️ Manual / Partial | ⚠️ Config file only | ⚠️ INI list only | ⚠️ INI list only |
| **Interactive GUI Executable Picker** | ✅ **Yes (`select_exe.ps1`)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Smart Multi-Engine Detection (Unity/Unreal/Godot/Native)** | ✅ **Yes (Automated)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Modular Epic Online Services (EOS / RedboneEOS)** | ✅ **Yes (Modular v3)** | ⚠️ Custom patches | ❌ No | ❌ No | ❌ No |
| **Automatic Windows Defender Firewall Rules** | ✅ **Yes** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Steam Non-Steam Shortcut Auto-Injector** | ✅ **Yes (`shortcuts.vdf`)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Clean Zero-Trace Uninstaller & Game Restorer** | ✅ **Yes (1-Click)** | ❌ No | ❌ No | ❌ No | ❌ No |
| **Digitally Signed Binaries** | ✅ **Yes (BlueStar Developers)** | ❌ No | ❌ No | ❌ No | ❌ No |

---

## 🔥 What's New in v1.3.2 (The Good Stuff)

* **Rock-Solid Networking Core:**  
  We went back to our clean, battle-tested network foundation and scrapped invasive network hooks that were causing WAN regressions and silent packet drops. UDP traffic, direct P2P connections, and Steam Datagram Relay (SDR) route naturally and reliably.

* **Fixed Steam Matchmaking & Server Browsers:**  
  * Fixed an elusive low-level ABI mismatch on `ISteamMatchmaking::GetLobbyByIndex`, preventing server browser crashes when iterating lobby results.
  * Server browsers now populate reliably across Unity (IL2CPP / Mono) and Unreal games (tested thoroughly on stubborn titles like *Shift At Midnight*).
  * Decoupled distance filter overrides so games that manage their own search queries (Close, Default, Worldwide) don't get choked or timed out by the proxy.
  * Seamless AppID consistency: games query real metadata without losing Spacewar 480 lobby routing.

* **Modular Epic Online Services (EOS v3 & RedboneEOS):**  
  Got an Unreal Engine game or title utilizing Epic Online Services? ReFix includes modular `EOSSDK-Win64-Shipping.dll` and `RedboneEOS.dll` proxies that handle device auth, session handshakes, and ticket validation. If your game doesn't use EOS, those modules stay out of the way completely.

* **Steam Overlay & Friend Invitations:**  
  Full support for Shift+Tab Steam Overlay. In-game invitations automatically inject connection parameters (`+connect_lobby`), letting your friends click "Join Game" directly from their Steam friends list to hop straight into your session.

* **Digitally Signed Binaries:**  
  Every release binary (`steam_api64.dll`, `winmm.dll`, `EOSSDK-Win64-Shipping.dll`, `RedboneEOS.dll`) is digitally signed with our official **Corøna (BlueStar Developers)** Authenticode certificate. This keeps Windows Defender and other security software from freaking out over false positives.

---

## 🎮 How It Works

### Mode 1 — ReFix Online via Steam (Spacewar 480)
* Hooks straight into your running Steam client by masking your game under Valve's Spacewar (AppID 480).
* Lets you use real Steam infrastructure: global server lists, P2P NAT punch-through, Steam Relay, in-game invites, and the Shift+Tab overlay.
* Automatically injects the game's real AppID into lobby metadata so you only match with other players running the same game.

### Mode 2 — Re:Goldberg LAN without Steam (100% Offline)
* Powered by the modern [gbe_fork](https://github.com/Detanup01/gbe_fork) / Goldberg backend.
* Run your game without Steam installed or running at all!
* Automatically broadcasts on your local subnet UDP port (`47584`) for instant discovery on home LANs or virtual networks (like Radmin VPN, Hamachi, or ZeroTier).
* Saves your game data locally in a clean, portable `saves/` folder.

### Universal DLC Unlocker (BLUESTAR Engine)
* Built right on top of [SmokeAPI](https://github.com/acidicoala/SmokeAPI) and [CreamAPI](https://github.com/acidicoala/CreamAPI).
* Three easy options:
  * **Unlock All:** Instantly activates every piece of DLC known to the Steam Store catalog.
  * **Unlock None:** Keeps the base game clean for vanilla testing.
  * **Pick & Choose:** Interactive scraper that pulls DLC names live from the Steam API so you can select exactly what you want.

---

## 🚀 Quick Start Guide

### For Gamers (Automatic 1-Click Setup)

1. Grab the latest **`ReFix_Release_v1.3.2.zip`** from [GitHub Releases](https://github.com/Coronitaa/ReFix/releases/tag/v1.3.2).
2. Extract the zip to any folder you like.
3. Run **`AutoDeploy.bat`**:
   - Use the file browser pop-up to select your game directory.
   - Confirm your game's `.exe`.
   - Pick your mode:
     - `[1] ReFix Online via Steam`: Play online with friends over Steam.
     - `[2] Re:Goldberg LAN without Steam`: Play offline on LAN with zero Steam required.
   - Choose your DLC preference (`[1] All`, `[2] None`, `[3] Custom`).
4. Launch your game and have fun!

### For DLC Unlocking Only

1. Run **`DLC_Unlocker.bat`**.
2. Select your game folder.
3. Choose whether to unlock all DLCs, choose specific ones, or reset back to vanilla.

### Want to Uninstall or Restore Everything?

No stress! Just run **`Uninstall_ReFix.bat`**, select your game folder, and it will safely restore all original DLLs (`.orig`, `_valve.dll`, `_o.dll`), remove all proxies and configs, and leave your game completely untouched.

---

## 📂 Project Structure

```
ReFix/
├── AutoDeploy.bat                      # 1-Click universal auto-deploy setup tool
├── DLC_Unlocker.bat                    # Universal DLC Unlocker (SmokeAPI / CreamAPI)
├── Uninstall_ReFix.bat                 # Zero-trace uninstaller & clean game restorer
├── build.bat                           # 1-Click MSVC build script for all C++ proxies
├── deploy.bat                          # Packaging script for releases
├── ReFix.ini                           # Central configuration template
├── README.md                           # Documentation & quick start guide
├── bin/                                # Deployment binaries and helper scripts
│   ├── steam_api64.dll                 # ReFix Steamworks proxy (Signed)
│   ├── winmm.dll                       # Startup loader & overlay hook (Signed)
│   ├── EOSSDK-Win64-Shipping.dll       # Epic Online Services auth proxy (Signed)
│   ├── RedboneEOS.dll                  # Redpoint EOS bridge proxy (Signed)
│   ├── ReFixSync.dll                   # Synchronization helper
│   ├── detect_game.ps1                 # Smart engine and binary detector
│   ├── deploy_helper.ps1               # Deployment and configuration synchronizer
│   ├── select_dlcs.ps1                 # Steam Store DLC scraper
│   ├── dlc_unlocker.ps1                # SmokeAPI/CreamAPI deployment manager
│   ├── select_folder.ps1               # GUI folder selection dialog
│   ├── select_exe.ps1                  # GUI executable picker dialog
│   ├── apply_firewall.ps1              # Windows Defender Firewall helper
│   ├── add_steam_shortcut.ps1          # Steam shortcuts.vdf binary injector
│   ├── goldberg/                       # Standalone Goldberg LAN emulator files
│   ├── bepinex/                        # BepInEx runtime loader
│   └── tools/                          # Steamless unpacking utilities
└── src/                                # Complete C++ source code
    ├── winmm_proxy.cpp                 # winmm.dll loader & hooking entry point
    ├── steam_proxy.cpp                 # steam_api64.dll proxy & Steamworks wrapper
    ├── eos_proxy.cpp                   # Epic Online Services session & auth emulator
    ├── unreal_detect.cpp               # Unreal Engine subsystem detection
    ├── unreal_steam_emu.cpp            # Unreal Engine Steam adapter
    ├── upnp_firewall.cpp               # Firewall automation routines
    ├── server_browser_gui.cpp          # In-game ImGui server browser
    ├── include/                        # Steamworks SDK headers
    └── minhook/                        # MinHook library
```

---

## ⚙️ Configuration Reference (`ReFix.ini`)

You can tweak game-specific behavior anytime by opening `ReFix.ini` in your game's directory:

```ini
[Game]
GameName=GenericGame            ; Game title for logging and window titles
EngineType=Auto                 ; Auto | Unity | Unreal | Godot | Native

[Online]
Mode=valve                      ; valve (Steam Online 480) | goldberg (LAN Offline)

[Steam]
MaskAppId=480                   ; Steam AppID used for masking (Default: Spacewar 480)
RealAppId=                      ; Real Steam AppID for DLCs and metadata (e.g. 550)
Language=english                ; Game language
BypassLicenseCheck=true         ; Allow running without Steam store ownership
DLCs=all                        ; all | none | comma-separated AppIDs

[Matchmaking]
EnableLobbyFilter=false         ; Filter lobbies by custom metadata key
LobbyFilterKey=game_filter      ; Metadata key for game filtering
LobbyFilterValue=               ; Custom filter value (defaults to RealAppId)
LobbyDistanceFilter=Worldwide   ; Close | Default | Far | Worldwide
MaxLobbyResults=50              ; Max lobbies to return in server browser

[Overlay]
EnableOverlay=true              ; Enable Steam Shift+Tab overlay hook
OverlayAppId=480                ; AppID for overlay initialization

[EOS]
DeviceIdAuth=true               ; Emulate Epic Online Services DeviceID login

[User]
Name=Player                     ; Custom player name (or leave empty for auto)
SteamId=                        ; Custom SteamID64 (or leave empty for auto)

[Network]
ListenPort=47584                ; UDP port for LAN discovery
CustomBroadcasts=               ; Extra broadcast IPs for VPNs (comma-separated)
```

---

## 🛠️ Building from Source

### Prerequisites
* **Windows 10 / 11 (64-bit)**
* **Visual Studio 2022** (Community, Professional, or Enterprise) with the **Desktop development with C++** workload installed (includes MSVC v143 and MASM `ml64.exe`).

### 1-Click Compilation
Open a **Developer Command Prompt for VS 2022** (x64) and simply run:
```cmd
build.bat
```
`build.bat` takes care of the whole pipeline:
1. Detects your MSVC environment automatically via `vswhere.exe`.
2. Assembles the forwarding tables (`winmm_fwd.asm`, `eos_fwd.asm`, `steam_fwd.asm`) using `ml64.exe`.
3. Builds `winmm.dll`, `EOSSDK-Win64-Shipping.dll`, `RedboneEOS.dll`, and `steam_api64.dll` with full optimization (`/O2 /EHsc /LD`).
4. Outputs the finished binaries to `build/` and copies them directly into `bin/`.

---

## 💖 Credits & Big Thanks

ReFix wouldn't be possible without the incredible work done by the open-source and modding communities:

* **[Mr_Goldberg](https://gitlab.com/Mr_Goldberg/goldberg_emulator)** — The legend who pioneered open-source Steamworks emulation.
* **[Detanup01 & gbe_fork contributors](https://github.com/Detanup01/gbe_fork)** — Outstanding modern enhancements to Goldberg LAN emulation.
* **[acidicoala](https://github.com/acidicoala)** — Creator of SmokeAPI and CreamAPI, the gold standards of DLC unlocking.
* **[TsudaKageyu](https://github.com/TsudaKageyu/minhook)** — The clean, minimalistic MinHook library.
* **[ocornut](https://github.com/ocornut/imgui)** — Dear ImGui for immediate-mode GUI magic.
* **[atom0s](https://github.com/atom0s/Steamless)** — The invaluable Steamless DRM unpacker.

---

## ⚖️ License

Distributed under the **Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)** License.

* **Share & Adapt**: You're free to copy, modify, and build upon this project.
* **Non-Commercial**: Strictly for non-commercial and educational use. Keep it free!
* **ShareAlike**: If you distribute modified versions, keep them under this same open license.

See `LICENSE` for the full legal text.
