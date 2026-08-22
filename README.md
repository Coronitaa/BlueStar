# BlueStar Launcher

> **Universal Steam Game Instance Manager, Depot Staging, Manifest Resolver, and Multi-Engine Launcher**

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-brightgreen.svg)]()
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0%20WPF-purple.svg)]()
[![Release: v1.0.0](https://img.shields.io/badge/Release-v1.0.0-orange.svg)](https://github.com/Coronitaa/BlueStar/releases)

BlueStar is an all-in-one desktop game management and launcher ecosystem designed for modern PC gaming. It seamlessly integrates Steam depot downloads, manifest staging, automated engine detection, multi-engine emulation, DLC unlocking, prerequisite management, and mod injection into a clean, modern, and dark-themed interface.

---

## Downloads and Installation

You can download the latest version from the [Releases](https://github.com/Coronitaa/BlueStar/releases) page:

| Edition | File | Description |
| :--- | :--- | :--- |
| **Windows Setup (Recommended)** | `BlueStar-Setup-v1.0.0-win-x64.exe` | Standard Windows installer with start menu shortcuts, desktop icons, and uninstaller. |
| **Portable Edition** | `BlueStar-v1.0.0-Portable-win-x64.zip` | Standalone zero-install archive. Extract anywhere and launch `BlueStar.exe`. |

> [!NOTE]
> All release binaries are digitally signed with an Authenticode certificate issued to **Corøna (BlueStar Developers)**.

---

## Key Features

### Explore and DepotBox Integration
- Browse hundreds of games with metadata, banner art, system compatibility tags, and DLC counts.
- Real-time SteamDB integration for accurate depot release dates and version resolution.
- One-click instance creation and background downloading.

### Instance Lifecycle and Depot Updates
- Manage multiple independent game installations without file conflicts.
- **Smart Update Resolver**: Detect newer manifests on DepotBox and apply updates non-destructively while preserving save games, mods, and emulator configurations.
- Direct quick actions: launch game, open folder, generate desktop/Steam shortcuts, create mobile companion QR codes, or safely delete files.

### Multi-Engine Multiplayer and Emulation
- **ReFix Online (Steam Spacewar)**: Full Steamworks Spacewar (AppID 480) multiplayer proxy support with Steam friends list, invites, and online lobbies.
- **Re:Goldberg LAN**: Pure standalone, offline, and LAN multiplayer emulation requiring zero internet connection or Steam client.
- **Community Ratings**: Real-time crowd-sourced voting on emulation compatibility and best-working modes for each title.

### Automated DLC Entitlement
- Integrated SmokeAPI and CreamInstaller pipelines.
- Automatically generates and writes `cream_api.ini` and `SmokeAPI.config.json` with support for all known game DLCs.
- Clean one-click installation and restoration.

### 1-Click Windows Game Prerequisites
- Automatically scans the host OS and game directory for missing runtimes:
  - Visual C++ 2015–2022 Redistributable (x86 & x64)
  - DirectX End-User Runtimes (Legacy D3DX9 / XAudio2)
  - .NET Desktop Runtime 8.0 (x64)
  - Unreal Engine Prerequisites
- Supports silent batch installation and interactive setup wizards.

### Modding and BepInEx Framework
- Native engine detection for Unity (Mono / IL2CPP), Unreal Engine, Godot, and Native executables.
- One-click BepInEx installation with auto-configured `doorstop_config.ini` and folder structures.
- Steam Workshop preview browser and mod management.

### Built-in Auto-Updates
- Integrated update checker notifying users of new BlueStar releases published to GitHub.
- Background downloading with seamless one-click restart and upgrade.

---

## System Requirements

- **Operating System**: Windows 10 (Build 19041+) or Windows 11 (64-bit)
- **Architecture**: x64 (64-bit)
- **Framework**: .NET 8.0 Desktop Runtime (pre-bundled in self-contained releases)
- **Optional**: Active Steam Client (required only for Steam Spacewar online multiplayer mode)

---

## Solution Architecture

```
BlueStar/
├── src/
│   ├── BlueStar.Core/             # Domain models, contracts, and interfaces
│   │   ├── Models/                # GameInstance, DepotInfo, DlcInfo, Engine, etc.
│   │   └── Interfaces/            # IInstanceManager, IEmulator, IDlcInstaller, etc.
│   ├── BlueStar.Infrastructure/   # Implementation of business logic & storage
│   │   ├── DepotBox/              # DepotBox REST client, Lua parser, archive reader
│   │   ├── Downloader/            # DepotDownloader wrapper & Steam manifest helper
│   │   ├── Emulators/             # ReFix suite, Goldberg, SmokeAPI, Rating service
│   │   ├── Engine/                # Unity, Unreal, Godot PE & assembly detector
│   │   ├── Mods/                  # BepInEx injector & game mod resolvers
│   │   ├── Services/              # Prerequisites, Auth, Notifications, Licensing
│   │   └── Storage/               # AppSettingsService & DPAPI SecureStorage
│   └── BlueStar.App/              # WPF Modern Dark UI application
│       ├── Views/                 # Home, Explore, Library, InstanceDetail, Settings, About
│       ├── ViewModels/            # MVVM CommunityToolkit ViewModels
│       └── Themes/                # DarkTheme XAML styles, brushes, typography, icons
├── tests/
│   ├── BlueStar.Core.Tests/       # Unit tests for domain logic
│   └── BlueStar.Infrastructure.Tests/ # Integration tests for parsers, engines, and managers
└── workers/
    └── emulator-ratings/          # Cloudflare Worker for community emulator ratings
```

---

## Configuration and Settings

BlueStar is designed to work right out of the box with zero required configuration:
- **Default Backend API**: Pre-configured with the default DepotBox backend service.
- **Personal API Key Override**: Power users can optionally enter their personal API key in `Settings -> API Configuration` to override the default backend.
- **Storage Directory**: Games and instances are safely stored in `%AppData%\BlueStar\instances`.

---

## Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (optional, for compiling the installer)

### Build and Run

```bash
# 1. Clone the repository
git clone https://github.com/Coronitaa/BlueStar.git
cd BlueStar

# 2. Run all unit and integration tests (107 tests)
dotnet test

# 3. Launch the development build
dotnet run --project src/BlueStar.App
```

### Packaging Release Binaries

```powershell
# Publish self-contained win-x64 release
dotnet publish src/BlueStar.App/BlueStar.App.csproj -c Release -r win-x64 --self-contained true -o dist/publish

# Compile Setup Installer executable
& "ISCC.exe" build/installer.iss
```

---

## Credits and Attributions

BlueStar proudly relies on and thanks the following open-source projects:

- **ReFix Suite** by [Coronitaa](https://github.com/Coronitaa/ReFix) — Multi-engine multiplayer fix & deployment suite
- **DepotDownloader** by [SteamRE](https://github.com/SteamRE/DepotDownloader) — Steam depot downloading tool
- **SmokeAPI** by [acidicoala](https://github.com/acidicoala/SmokeAPI) — Universal Steamworks DLC entitlement emulator
- **CreamInstaller** by [pointfeev](https://github.com/pointfeev/CreamInstaller) — Automatic DLC unlocker installer
- **BepInEx** by [BepInEx Team](https://github.com/BepInEx/BepInEx) — Unity and .NET game plugin framework
- **Goldberg Emulator** by [Mr_Goldberg](https://gitlab.com/Mr_Goldberg/goldberg_emulator) & [Detanup01](https://github.com/Detanup01/gbe_fork) — Steam LAN emulator
- **SteamKit2** by [SteamRE](https://github.com/SteamRE/SteamKit) — .NET library for Steam network communication
- **DepotBox** — Depots and manifest index service

---

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).  
Copyright (C) 2026 **Corøna** & BlueStar Developers.


