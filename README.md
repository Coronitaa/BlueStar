# BlueStar Launcher

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-brightgreen.svg)]()
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0%20WPF-purple.svg)]()
[![Release: v1.5.7](https://img.shields.io/badge/Release-v1.5.7-orange.svg)](https://github.com/Coronitaa/BlueStar/releases/latest)

BlueStar is a Windows launcher for Steam games. It finds a game in its catalog, downloads it from Steam depots, installs what the game needs to run, sets up multiplayer with [ReFix](https://github.com/Coronitaa/ReFix), unlocks DLCs and manages mods, all from one app.

## Download

Get the latest version from [Releases](https://github.com/Coronitaa/BlueStar/releases/latest).

| Package | File | Notes |
| :--- | :--- | :--- |
| Setup (recommended) | `BlueStar-v1.5.7-Setup-win-x64.exe` | Installs for your user without asking for administrator rights. Installs the Visual C++ runtimes if they are missing, and WebView2 only if the PC doesn't have it. |
| Portable | `BlueStar-v1.5.7-Portable-win-x64.zip` | Extract and run `BlueStar.exe`. Includes WebView2 and the Visual C++ runtime DLLs. |
| MSIX | `BlueStar-v1.5.7-win-x64.msix` | Windows app package. |

Every package includes .NET 8, so there is nothing else to install.

<img width="1432" height="875" alt="BlueStar screenshot" src="https://github.com/user-attachments/assets/839d778f-39fd-4d7e-aa96-2e43800623d6" />
<img width="1429" height="872" alt="BlueStar screenshot" src="https://github.com/user-attachments/assets/c6465f14-2a4c-4c63-8809-3c4c304fa144" />
<img width="1437" height="874" alt="BlueStar screenshot" src="https://github.com/user-attachments/assets/ed4adc8e-f764-40f3-ba01-dbf04e5a76d9" />
<img width="1434" height="870" alt="BlueStar screenshot" src="https://github.com/user-attachments/assets/c1fc7ba5-a8e9-4b85-ba7c-259e0443c971" />
<img width="1422" height="877" alt="BlueStar screenshot" src="https://github.com/user-attachments/assets/650239fc-2c88-40d4-9323-f7df2f9dfb0c" />

## What it does

**Catalog.** The catalog is a local SQLite snapshot (`catalog.sqlite.zst`), so search and filters work instantly and offline. Each game shows its DRM, launcher and Steam reviews. Explore can sort by downloads in the BlueStar community; sorting by release date leaves out games that are not out yet, and Coming soon only lists those.

**Downloads.** BlueStar downloads games from Steam depots with its own build of DepotDownloader, using manifests from DepotBox and ManifestHub. The installer only offers the depots and DLCs that match your Windows and its architecture. You can also pick the Steam download server in Settings (26 regions, a custom Cell ID, or automatic), or import a depot ZIP by dragging it in.

**Library.** Games installed by BlueStar, games in your Steam libraries and games in any folder you add show up in one library. BlueStar finds the executable and the engine (Unity Mono/IL2CPP, Unreal, Godot or native) by itself.

**Multiplayer with ReFix 2.4.3.** One click installs ReFix into a game, in one of three modes:
- **ReFix LAN + Online** (recommended) plays on your local network with no Steam client, and lets the game reach the Internet when it needs to. It has its own in-game overlay (Shift+Tab or the controller's Xbox / PS button) to see players on the network, join them and invite them, and player profiles with their own name, picture, SteamID and saves. With Steam installed, the overlay's Steam button adds your Steam friends to the same rooms.
- **ReFix LAN Offline** is the previous ReFix LAN: local network only, no Steam services.
- **ReFix Online Legacy** is the previous ReFix Online: plays over the Internet through the Steam client.

BlueStar never asks for administrator rights while deploying ReFix. Each game page shows how other players rated ReFix in that game, and BlueStar asks for your own rating when you close the game.

**DLCs.** Unlocks DLCs with SmokeAPI and CreamInstaller.

**Mods.** The Mods tab installs mods from the Steam Workshop, Thunderstore, Nexus Mods and CurseForge, or from a `.zip`, `.7z` or `.dll` you drop onto it.
- Every installed file is tracked with its SHA-256. Game files a mod replaces are backed up, and a failed install rolls the game folder back.
- Games with their own mods folder (RimWorld, Don't Starve Together, Project Zomboid) get the mod there. Unity games get BepInEx installed when a mod needs it.
- Modpacks export to a `.zip` with every mod file, and the BepInEx config for Unity games.

**Prerequisites.** Checks for Visual C++ 2015-2022 (x64 and x86), WebView2, DirectX, the .NET Desktop Runtime and Unreal Engine prerequisites, and installs the ones a game needs from official sources.

**Languages.** English and Spanish. BlueStar starts in Spanish if Windows is in Spanish, and in English otherwise. You can change it in Settings.

## Requirements

- Windows 10 (64-bit, build 19041 or later) or Windows 11
- The Steam client, only to play with ReFix Online Legacy or to add Steam friends in ReFix LAN + Online

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). [Inno Setup 6](https://jrsoftware.org/isinfo.php) is only needed to build the Setup.

```bash
git clone https://github.com/Coronitaa/BlueStar.git
cd BlueStar
dotnet test
dotnet run --project src/BlueStar.App
```

API keys for DepotBox and the mod providers are not in the repository. For local runs, copy `modkeys.example.json` to `modkeys.json` and fill in your own keys. Both `modkeys.json` and `depotbox.key` are ignored by git.

To build the release packages (Setup, Portable and MSIX):

```powershell
.\build-release.ps1 -Version 1.5.7
```

Run it without `-Version` and it asks for the version. Release builds store the keys encrypted inside `bluestar.dat`.

## Project layout

```
src/
  BlueStar.App/               WPF app
    tools/ReFix_deploy/       bundled ReFix release
  BlueStar.Core/              models and interfaces
  BlueStar.Infrastructure/    catalog, downloads, Steam, engine detection, mods, emulators, updater
  BlueStar.DepotDownloader/   DepotDownloader fork
  BlueStar.CatalogBuilder/    builds the catalog snapshot
  BlueStar.Package/           MSIX packaging
tests/
  BlueStar.App.Tests/
  BlueStar.Core.Tests/
  BlueStar.Infrastructure.Tests/
build-release.ps1             builds every package
installer.iss                 Inno Setup script
```

## Credits

Projects by the same author:
- [ReFix](https://github.com/Coronitaa/ReFix): the multiplayer fix BlueStar deploys.
- [DepotDownloaderMod](https://github.com/Coronitaa/DepotDownloaderMod): the DepotDownloader fork BlueStar uses.
- [BlueStar-Catalog](https://github.com/Coronitaa/BlueStar-Catalog): publishes the catalog snapshots.

Other projects and services:
- [SteamKit2](https://github.com/SteamRE/SteamKit) and [DepotDownloader](https://github.com/SteamRE/DepotDownloader) by SteamRE
- [SmokeAPI](https://github.com/acidicoala/SmokeAPI) by acidicoala
- [CreamInstaller](https://github.com/pointfeev/CreamInstaller) by pointfeev
- [Goldberg Emulator](https://gitlab.com/Mr_Goldberg/goldberg_emulator) by Mr_Goldberg and [gbe_fork](https://github.com/Detanup01/gbe_fork) by Detanup01
- [BepInEx](https://github.com/BepInEx/BepInEx)
- [ManifestHub2](https://github.com/SSMGAlt/ManifestHub2) by SSMGAlt
- [DepotBox](https://depotbox.org)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Polly](https://github.com/App-vNext/Polly), [Serilog](https://github.com/serilog/serilog), [QRCoder](https://github.com/codebude/QRCoder)

## License

[CC BY-NC-SA 4.0](LICENSE). Copyright (C) 2026 Corøna and BlueStar contributors.
