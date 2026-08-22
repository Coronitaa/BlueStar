# BlueStar — Architecture & Developer Guide

BlueStar is a generic, independent Steam launcher designed to manage, download, and launch games using DepotBox metadata archives and raw manifest data.

---

## Technical Stack

- **Runtime**: .NET 8.0 LTS
- **UI Framework**: WPF (Windows Presentation Foundation) with MVVM pattern via CommunityToolkit.Mvvm
- **Design System**: Modrinth-inspired Dark Theme (`DarkTheme.xaml`)
- **Logging**: Serilog (Rolling file sink + Console)
- **Security**: Windows DPAPI (`System.Security.Cryptography.ProtectedData`) for sensitive token/API key storage
- **DLC Management**: Wraps `CreamInstaller.exe` directly as a subprocess without altering its installation mechanics

---

## Solution Structure

```
BlueStar.sln
├── src/
│   ├── BlueStar.Core/               Domain models, enums, interfaces
│   ├── BlueStar.Infrastructure/     DepotBox parsers, REST client, Storage, Cache, Downloader, DLC, Updater
│   └── BlueStar.App/                WPF UI, ViewModels, Views, Themes, Converters, DI Root
├── tests/
│   ├── BlueStar.Core.Tests/         Unit tests for Core domain models
│   └── BlueStar.Infrastructure.Tests/ Unit tests for Infrastructure parsers
├── installer/
│   └── BlueStar.iss                 Inno Setup compilation script
└── .github/workflows/
    └── ci-cd.yml                    GitHub Actions CI/CD workflow
```

---

## Key Infrastructure Components

### 1. `DepotBoxLuaParser`
- Parses DepotBox metadata `.lua` files.
- Uses compiled regular expressions for `addappid()` and `setManifestid()`.
- Extracts game and DLC AppIDs, DepotIDs, ManifestIDs, DepotKeys, and flags.

### 2. `DepotBoxArchiveParser`
- Validates and parses `.zip` archives exported from DepotBox.
- Ensures binary `.manifest` files match declared manifest IDs.

### 3. `DepotBoxApiClient`
- Handles REST communication with the DepotBox service (`https://depotbox.org`).
- Managed via `HttpClient` configured in DI with a 15-minute timeout.

### 4. `DepotDownloaderProvider`
- Wraps the DepotDownloader CLI tool (`DepotDownloader.exe`).
- Executes parallel/sequential depot downloads with progress reporting and cancellation support.

### 5. `CreamInstallerService`
- Wraps `CreamInstaller.exe` to manage DLC unlockers.
- Preserves CreamInstaller's native behavior per specification.

### 6. `GitHubUpdateService`
- Checks GitHub Releases (`Coronitaa/BlueStar`) for new versions.
- Downloads installer updates and executes unattended upgrades.

---

## Design System

The application uses a custom WPF theme located in `BlueStar.App/Themes/DarkTheme.xaml`:
- Primary Accent: `#1BD96A` (Modrinth Green)
- Base Background: `#111113`
- Surface Cards: `#18181B`
- Border Color: `#2E2E36`

---

## Building and Testing

```bash
# Build solution
dotnet build BlueStar.sln

# Run test suite
dotnet test BlueStar.sln

# Publish self-contained executable
dotnet publish src/BlueStar.App/BlueStar.App.csproj -c Release -r win-x64
```
