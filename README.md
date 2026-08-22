# ⭐ BlueStar Launcher

> **Universal Steam Game Instance Manager, Depot Staging, Manifest Resolver, and Multi-Engine Launcher**

[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-brightgreen.svg)]()
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0%20WPF-purple.svg)]()

---

## ✨ Features

- **🎮 Explore & DepotBox Integration**: Search games, explore manifests, filter by OS/type, inspect DLC count, and preview real Steam release history.
- **📦 Instance Lifecycle Management**: Independent game instances, non-destructive manifest updates, custom launch parameters, and clean uninstallers.
- **🚀 Multi-Engine Emulation & Multiplayer**:
  - **ReFix Online**: Integrated Steamworks Spacewar (AppID 480) multiplayer fix with Steam friend invites and online lobby support.
  - **Re:Goldberg LAN**: 100% standalone, offline, and LAN multiplayer without requiring Steam client or network connectivity.
- **🔓 Automated DLC Unlocking**: Built-in SmokeAPI and CreamInstaller integrations for automatic DLC unlock generation and configuration.
- **🛠️ 1-Click Game Prerequisites**: Automatic system detection and silent/interactive installers for Visual C++ 2015–2022, DirectX End-User Runtimes, .NET Desktop Runtime, and Unreal Engine prerequisites.
- **🧩 Modding & Plugin Ecosystem**: Integrated BepInEx injection, Unity patchers, Workshop preview downloader, and save backup management.
- **🔄 Auto-Update System**: Seamless in-app update checks and notifications powered by GitHub Releases.

---

## 💻 System Requirements

- **Operating System**: Windows 10 / Windows 11 (64-bit)
- **Runtime**: .NET 8 Desktop Runtime (included in self-contained portable and setup releases)
- **Optional**: Steam Client (for Steam Spacewar online multiplayer modes)

---

## 🛠️ Building from Source

```bash
# Clone the repository
git clone https://github.com/Coronitaa/BlueStar.git
cd BlueStar

# Restore and run tests
dotnet test

# Build and run the WPF application
dotnet run --project src/BlueStar.App
```

### Packaging Releases

```powershell
# Publish self-contained win-x64 build
dotnet publish src/BlueStar.App/BlueStar.App.csproj -c Release -r win-x64 --self-contained true -o dist/publish

# Compile Inno Setup installer
& "ISCC.exe" build/installer.iss
```

---

## 📜 Credits & Third-Party Projects

BlueStar is built upon and inspired by amazing open-source projects in the gaming and emulation community:

- **ReFix Suite** by Coronitaa
- **DepotDownloader** by SteamRE
- **SmokeAPI** by acidicoala
- **CreamInstaller** by pointfeev
- **BepInEx** by BepInEx Team
- **Goldberg Emulator** by Mr_Goldberg & Detanup01
- **SteamKit2** by SteamRE
- **DepotBox** API Service

---

## 📄 License

Licensed under the [GNU General Public License v3.0](LICENSE).
Author: **Corøna** & BlueStar Developers.
