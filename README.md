# BlueStar Launcher

> **Universal Steam Game Instance Manager, Depot Staging, Manifest Resolver, and Multi-Engine Launcher**

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-brightgreen.svg)]()
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0%20WPF-purple.svg)]()
[![Release: v1.4.1](https://img.shields.io/badge/Release-v1.4.1-orange.svg)](https://github.com/Coronitaa/BlueStar/releases/latest)
[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen.svg)](https://github.com/Coronitaa/BlueStar/actions)

BlueStar is an all-in-one desktop game management and launcher ecosystem for modern PC gaming. It brings together Steam depot downloads, manifest staging, engine detection, online and LAN multiplayer emulation, DLC unlocking, prerequisite management, and mod injection into a clean, modern dark-themed interface.

<img width="1441" height="882" alt="image" src="https://github.com/user-attachments/assets/676715de-f206-4924-b81e-8e119f5a60e6" />
<img width="1441" height="880" alt="image" src="https://github.com/user-attachments/assets/6ecb0fd0-bd65-40e5-a1ad-ed125e60003e" />
<img width="1437" height="867" alt="image" src="https://github.com/user-attachments/assets/add5b36b-2a93-4149-8110-c6ab8fd96586" />
<img width="1430" height="874" alt="image" src="https://github.com/user-attachments/assets/d5c2f015-0f7b-4e4d-b61d-8d6e0ee57b64" />
<img width="1434" height="949" alt="image" src="https://github.com/user-attachments/assets/a41cbe5c-3dbb-42aa-b335-33b3481d693c" />

---

## ⚡ Quick Download

<div align="center">

[![Download BlueStar](https://img.shields.io/badge/Download-BlueStar%20Latest%20Release-2ea44f?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/Coronitaa/BlueStar/releases/latest)

</div>

| Package | Asset Name | Download | Description |
| :--- | :--- | :---: | :--- |
| **Windows Setup (Recommended)** | `BlueStar-v1.4.1-Setup-win-x64.exe` | [![Download Setup](https://img.shields.io/badge/Download-Installer-0078D4?style=flat-square&logo=windows&logoColor=white)](https://github.com/Coronitaa/BlueStar/releases/download/v1.4.1/BlueStar-v1.4.1-Setup-win-x64.exe) | Standard Windows installer with start menu shortcuts, uninstaller, and update support. |
| **Portable Edition** | `BlueStar-v1.4.1-Portable-win-x64.zip` | [![Download Portable](https://img.shields.io/badge/Download-ZIP-gray?style=flat-square&logo=archive&logoColor=white)](https://github.com/Coronitaa/BlueStar/releases/download/v1.4.1/BlueStar-v1.4.1-Portable-win-x64.zip) | Zero-install standalone archive. Extract anywhere and launch `BlueStar.exe`. |

> [!NOTE]
> All release packages are self-contained for 64-bit Windows systems (Windows 10 / 11). No separate .NET runtime installation required.

---

## 🚀 Key Features

- **Universal Game Instance Manager**: Manage standalone installations from DepotBox, Steam libraries, or local folders. Supports automatic executable detection and native Steam protocol fallback (`steam://rungameid/{appId}`).
- **Offline-First Catalog & Instant Search**: Ultra-fast catalog browsing powered by a local compressed SQLite snapshot (`catalog.sqlite.zst`), real-time faceted filters, DRM and launcher detection, and color-coded Steam reviews.
- **Steam Depot Downloader & Manifest Staging**: Integrated with `DepotDownloaderMod` for high-throughput chunk streaming, drag-and-drop depot ZIP import, manifest verification, and custom build staging.
- **Multi-Engine Emulation & Multiplayer**:
  - **ReFix Online**: Play online multiplayer via Steamworks Spacewar (AppID 480) with friends list, invites, and lobbies.
  - **Re:Goldberg LAN**: Fully offline and LAN multiplayer with zero Steam dependency.
  - **Community Ratings**: Compatibility feedback and player ratings per title.
- **1-Click DLC Unlocking**: One-click entitlement through integrated SmokeAPI and CreamInstaller pipelines.
- **Windows Prerequisites Manager**: Scans and silently installs missing runtimes (Visual C++ 2015–2022, DirectX, .NET 8.0 Desktop, Unreal Engine prerequisites).
- **Engine Detection & Modding**: Detects Unity (Mono/IL2CPP), Unreal Engine, and Godot, with automated BepInEx injection.
- **Modern Modular UI**: Modern dark theme with modular settings cards, live debug console, and tag localization.

---

## 🛠️ System Requirements

- **OS**: Windows 10 (64-bit, Build 19041+) or Windows 11
- **Architecture**: x64
- **Runtime**: Pre-bundled (.NET 8.0 Desktop Runtime self-contained)
- **Optional**: Steam Client (only required for Steam Spacewar online multiplayer)

---

## 📦 Project Architecture

```
BlueStar/
├── src/
│   ├── BlueStar.Core/             # Models, contracts, interfaces & abstractions
│   ├── BlueStar.Infrastructure/   # Engine detection, emulators, caching, storage & APIs
│   ├── BlueStar.DepotDownloader/  # Embedded depot download & manifest engine (fork)
│   ├── BlueStar.CatalogBuilder/   # Snapshot generator & metadata compiler
│   └── BlueStar.App/              # Modern WPF dark-themed desktop application
├── tests/
│   ├── BlueStar.Core.Tests/       # Domain logic tests
│   └── BlueStar.Infrastructure.Tests/ # Integration tests for services & parsers
├── tools/
│   └── ReFix_deploy/              # Local deployment bundles for emulators and hooks
└── workers/
    └── emulator-ratings/          # Cloudflare Worker for community ratings
```

---

## 🔗 Resources & Credits

### Author's Repositories (BlueStar Ecosystem)
- **[ReFix](https://github.com/Coronitaa/ReFix)** by [Coronitaa](https://github.com/Coronitaa) — Multi-engine multiplayer deployment and emulation suite (Steam Spacewar AppID 480 proxy, hooks, and LAN emulation).
- **[DepotDownloaderMod](https://github.com/Coronitaa/DepotDownloaderMod)** by [Coronitaa](https://github.com/Coronitaa) — Specialized fork of DepotDownloader integrated into BlueStar for depot downloads, manifest resolution, and custom key staging.
- **[BlueStar-Catalog](https://github.com/Coronitaa/BlueStar-Catalog)** by [Coronitaa](https://github.com/Coronitaa) — Automated distribution pipeline and repository for compressed catalog database snapshots (`catalog.sqlite.zst`).

### Internal Tools & Modules
- **`tools/ReFix_deploy`**: Bundled emulator binaries, BepInEx injector scripts, and deployment helpers.
- **`build-release.ps1`**: Automated build, runtime bundling, and release packaging workflow.
- **`workers/emulator-ratings`**: Serverless backend for community compatibility votes.

### External Open-Source Projects & Services
- **[SteamKit2](https://github.com/SteamRE/SteamKit)** by [SteamRE](https://github.com/SteamRE) — .NET client library for Steam network protocol communication.
- **[DepotDownloader](https://github.com/SteamRE/DepotDownloader)** by [SteamRE](https://github.com/SteamRE) — Upstream Steam depot downloading utility.
- **[SmokeAPI](https://github.com/acidicoala/SmokeAPI)** by [acidicoala](https://github.com/acidicoala) — Universal Steamworks DLC entitlement emulator.
- **[CreamInstaller](https://github.com/pointfeev/CreamInstaller)** by [pointfeev](https://github.com/pointfeev) — Automated DLC unlocker and proxy installer.
- **[Goldberg Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator)** by Mr_Goldberg & **[Fork](https://github.com/Detanup01/gbe_fork)** by Detanup01 — Steam LAN and offline emulator.
- **[BepInEx](https://github.com/BepInEx/BepInEx)** by BepInEx Team — Plugin framework and runtime patcher for Unity and .NET games.
- **[ManifestHub2](https://github.com/SSMGAlt/ManifestHub2)** by SSMGAlt — Community Steam manifest repository and AES decryption keys.
- **[DepotBox](https://depotbox.org)** — Depots index and game availability metadata service.
- **Core .NET Libraries**: [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Polly](https://github.com/App-vNext/Polly), [Serilog](https://github.com/serilog/serilog), [QRCoder](https://github.com/codebude/QRCoder).

---

## 🔨 Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (optional, for compiling the installer)

```bash
# 1. Clone repository
git clone https://github.com/Coronitaa/BlueStar.git
cd BlueStar

# 2. Run tests
dotnet test

# 3. Launch application
dotnet run --project src/BlueStar.App
```

To create release packages:
```powershell
.\build-release.ps1
```

---

## 📄 License

This project is licensed under the [GNU General Public License v3.0](LICENSE).  
Copyright (C) 2026 **Corøna** & BlueStar Contributors.
