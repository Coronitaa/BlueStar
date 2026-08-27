# BlueStar Launcher

> **Universal Steam Game Instance Manager, Depot Staging, Manifest Resolver, and Multi-Engine Launcher**

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-brightgreen.svg)]()
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0%20WPF-purple.svg)]()
[![Release: v1.1.2](https://img.shields.io/badge/Release-v1.1.2-orange.svg)](https://github.com/Coronitaa/BlueStar/releases)

BlueStar is an all-in-one desktop game management and launcher ecosystem designed for modern PC gaming. It seamlessly integrates Steam depot downloads, manifest staging, automated engine detection, multi-engine emulation, DLC unlocking, prerequisite management, and mod injection into a clean, modern, and dark-themed interface.

---

## Downloads and Installation

You can download the latest version from the [Releases](https://github.com/Coronitaa/BlueStar/releases) page:

| Edition | File | Description |
| :--- | :--- | :--- |
| **Windows Setup (Recommended)** | `BlueStar-v1.1.2-Setup-win-x64.exe` | Standard Windows installer with start menu shortcuts, desktop icons, and uninstaller. |
| **Portable Edition** | `BlueStar-v1.1.2-Portable-win-x64.zip` | Standalone zero-install archive. Extract anywhere and launch `BlueStar.exe`. |

> [!NOTE]
> All release binaries are self-contained for 64-bit Windows systems (no prior .NET 8 runtime installation required).

---

## Key Features

### Instance Lifecycle and Origin Tracking
- Manage independent game installations from multiple sources: **DepotBox downloads**, **Steam library imports**, and **custom local folders**.
- Dedicated launch pipeline with automatic protocol fallbacks (`steam://rungameid/{appId}`) for Steam-origin games.
- Non-destructive manifest updates on DepotBox that preserve save files, mods, and emulator configurations.

### Dynamic Tagging and Filtering Engine
- Real-time tag generation based on platform, game engine, installation origin, update status, and active emulator.
- Quick-filter chips to instantly organize games by engine (Unity, Unreal Engine, Godot, Source, Custom), source, and status.

### Multi-Engine Multiplayer and Emulation Hub
- **ReFix Online (Steam Spacewar)**: Full Steamworks Spacewar (AppID 480) multiplayer proxy support with Steam friends list, invites, and online lobbies.
- **Re:Goldberg LAN**: Standalone, offline, and LAN multiplayer emulation requiring zero internet connection or Steam client.
- **Community Ratings**: Global compatibility ratings and voting system per game title with dynamic recommendation thresholds.

### Automated DLC Entitlement
- Automatic discovery of game DLCs via Steam metadata.
- Integrated **SmokeAPI** and **CreamInstaller** pipelines with one-click installation and restoration.

### Explore and Modern Catalog Feeds
- 9-slot horizontal scrolling category carousels with progressive loading for trending, top-played, top-rated, and newly updated titles.
- Ranked trending suggestion chips with solid-to-subtle visual hierarchy for fast searching.

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
- Conditional mod management based on verified engine compatibility.

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
│   │   ├── Models/                # GameInstance, InstanceOrigin, GameTag, CatalogCategory, etc.
│   │   └── Interfaces/            # IInstanceManager, ITagsService, IEmulatorLifecycleService, etc.
│   ├── BlueStar.Infrastructure/   # Implementation of business logic & storage
│   │   ├── DepotBox/              # DepotBox REST client, Lua parser, archive reader
│   │   ├── Downloader/            # DepotDownloader wrapper & Steam manifest helper
│   │   ├── Emulators/             # ReFix suite, Goldberg, EmulatorRatingService, ReFixUpdateService
│   │   ├── Engine/                # Unity, Unreal, Godot, Isaac, and Supergiant engine detector
│   │   ├── Launcher/              # GameLauncherService with Steam protocol fallback
│   │   ├── Services/              # BackgroundTaskService, TagsService, DepotBoxAuthService
│   │   └── Storage/               # AppSettingsService & DPAPI SecureStorage
│   └── BlueStar.App/              # WPF Modern Dark UI application
│       ├── Views/                 # Home, Browse, Library, InstanceDetail, Settings, About
│       ├── ViewModels/            # MVVM CommunityToolkit ViewModels
│       ├── Controls/              # GameCategoryCarousel, CircularProgressButton
│       └── Themes/                # DarkTheme XAML styles, brushes, typography, icons
├── tests/
│   ├── BlueStar.Core.Tests/       # Domain logic and serialization tests
│   └── BlueStar.Infrastructure.Tests/ # Integration tests for services, engines, and parsers
└── tools/
    └── cloudflare-worker/         # Cloudflare Worker for community ratings & catalog feeds
```

---

## Configuration and Settings

BlueStar is designed to work right out of the box:
- **Default Backend API**: Pre-configured with the default DepotBox backend service.
- **Personal API Key Override**: Power users can optionally enter their personal API key in `Settings -> API Configuration` to override the default backend.
- **Storage Directory**: Instances and manifests are safely managed in `%AppData%\BlueStar\instances`.

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

# 2. Run all tests
dotnet test

# 3. Launch the development build
dotnet run --project src/BlueStar.App
```

### Packaging Release Binaries

```powershell
# Run the automated release builder script
.\build-release.ps1
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
