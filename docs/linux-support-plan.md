# BlueStar on Linux: audit and plan (v3)

Base: `main` @ ea94366 (v1.5.8). Build branch: `feat/linux-support`. v1 was reviewed by an architect subagent (verdict REVISE); v2 got REVISE on 3 items; v3 folds in every required change from both rounds.

## 0. Decisions

| Question | Decision | Why |
|---|---|---|
| UI | Port to **Avalonia 12**, one ViewModel at a time into a shared `BlueStar.Presentation`; WPF frozen to bug fixes, retired at parity | WPF has no Linux runtime; WPF under Wine breaks rendering and WebView2. Rejected: Avalonia XPF (commercial licence, keeps WPF debt), Uno (further from WPF XAML), Linux-only second UI forever |
| Translator engine | **umu-launcher** (Proton outside Steam) by default; **ProtonDirect** fallback, auto-selected when umu cannot run (AppArmor userns block, no python3); **ThroughSteam** opt-in | umu gives Steam Linux Runtime + protonfixes without Steam and keeps BlueStar in control of the process. Asking Steam (CompatToolMapping) needs Steam closed to edit config.vdf and loses process tracking, so only ReFix Online Legacy forces it, behind a dialog that says why |
| Plain Wine / Lutris runners | Not in v1 | YAGNI: Proton covers it |
| .NET | Retarget all projects to **net10.0 (LTS)** first | .NET 8 support ended 2026-11-10; a new Linux build must not ship on an unpatched runtime |
| Native Linux depots | Not in v1; always Windows depots | EngineDetector, ReFix and BepInEx assume `.exe` / `steam_api*.dll` |
| DLC unlocker on Linux | Hidden in v1 | `dlc_unlocker.ps1` has no `.sh`; ReFix itself unlocks DLCs (`DLCs = all`) |
| Snap Steam | Detected, reported as unsupported | Confinement breaks launching Proton from outside |

## 1. Audit (Windows-only code that must change)

| Area | Evidence | Linux change |
|---|---|---|
| Startup | `App.xaml.cs:30-37,55-110` mutex + `FindWindow/SetForegroundWindow` + MessageBox; pipe server `App.xaml.cs:592` | Keep mutex + pipe; replace window-raise with an "activate" pipe message |
| DI root | `App.xaml.cs:166-300+` | Move to shared `services.AddBlueStar()` used by both heads |
| CLI | only `nxm://` handled | Add `--launch <instanceId>`: a headless path that skips the single-instance mutex, launches the game itself and stays alive until it exits (Steam "playing" status and gamescope focus need the game in its process tree); notifies a running BlueStar UI over the pipe for playtime/state |
| Steam path | `ShortcutHelper.GetSteamPath` registry (14 callers) | Linux branch inside the same static method: `~/.steam/steam`, `~/.local/share/Steam`, Flatpak `~/.var/app/com.valvesoftware.Steam/.local/share/Steam` |
| Steam running / user | `SteamStatusService.cs:71-92,130-144` registry `ActiveProcess` | Read `~/.steam/registry.vdf` (same `ActiveProcess/pid`, `ActiveUser`) |
| Steam start/stop | `SteamStatusService.cs:392-437` (steamhost64.exe, `steam.exe -silent`), `ShortcutHelper.cs:882-917` (`-shutdown`), duplicated in `LibraryViewModel.cs:1815`, `InstanceDetailViewModel.cs:5858` | One shared helper; Linux: `steam -silent` / `steam -shutdown`, no steamhost |
| Launcher | `GameLauncherService.cs:45-67` (Steam games run exe directly if present), `:104-126` Process.Start, `:208-212,249` Kill tree | Compat launch (section 2). Stop = `wineserver -k` with same WINEPREFIX + runner. `InstanceOrigin.Steam` on Linux: `steam://rungameid/<appid>` unless ReFix/an emulator is deployed; then direct through the compat runtime with that library's `compatdata/<appid>` as the prefix (`STEAM_COMPAT_DATA_PATH`), so saves stay with the user's Steam copy |
| ReFix deploy | `ReFixEmulator.cs:500-540` deploy_helper.ps1 with `-TargetDir/-ExeDir/-EngineType/-DLCs/-MaskAppId`; `:796` detect_game.ps1; `:553-580` apply_firewall.ps1; `:626` Uninstall_ReFix.bat | Run `AutoDeploy.sh <game root> <mode> <appid> <overlay>` with `REFIX_USERNAME`, `WINEPREFIX`. The .sh uses one path for both steam_api search and `ReFix.ini` (`AutoDeploy.sh:431-440`), wrong for Unreal (`Binaries/Win64`). Fix in C#, not in the script (ReFix updates overwrite the script, `ReFixUpdateService.cs:206,223`): after the .sh runs, move `ReFix.ini` + `steam_appid.txt` to the exe folder when it differs and apply `EmulatorIniHelper.ApplyOverrides` there, as Windows does (`ReFixEmulator.cs:591-595`). Upstream PR for an exe-dir argument, not blocking. Engine detection via C# `EngineDetector`. Firewall: skip + hint (UDP 47584) |
| ReFix updater | `ReFixUpdateService.cs:206` ZipFile extract | `chmod +x` `*.sh` after extract |
| DLC | `CreamInstallerService.cs:92,103,302,311` | Hidden on Linux v1 |
| Mods: BepInEx | `BepInExService.cs:194` winhttp doorstop | Auto override `winhttp=n,b` |
| Mods: SteamCMD | `SteamCmdWorkshopClient.cs:39,132-155` + `WinTrust.cs:47` Authenticode | Linux tarball over HTTPS from Valve + pinned SHA-256; needs 32-bit glibc (requirements check) |
| Mods: Nexus nxm | registry handler | `.desktop` + `xdg-mime default ... x-scheme-handler/nxm` |
| Secrets | `SecureStorage.cs:14,45,69` DPAPI | Linux impl of existing `ISecureStorage`: Secret Service (libsecret over D-Bus) when available, else plain file `0600` documented as such. No fake encryption |
| Linker | `Win32Linker.cs` | Linux impl of existing `IWin32Linker`: `File.CreateSymbolicLink` + libc `link()` |
| Shortcuts | `.lnk` via WScript COM `ShortcutHelper.cs:1018,1043,1189`; shortcuts.vdf entries point to the game exe | `.desktop` and every "Add to Steam" shortcut run `"$APPIMAGE" --launch <id>` (never `Environment.ProcessPath`, which points into `/tmp/.mount_*`); PNG icon export from `IconConversionHelper`. Only ThroughSteam (section 2) writes an exe shortcut with CompatToolMapping |
| Exe icons | `InstanceDetailViewModel.cs:10600-10611` user32 `PrivateExtractIconsW` | Managed PE icon resource reader (no P/Invoke) or skip on Linux |
| Prerequisites | `PrerequisiteService` already optional at `MainViewModel.cs:237`, `SettingsViewModel.cs:435`, `InstanceDetailViewModel.cs:2875` | Not registered on Linux. "Run winetricks" button instead |
| Defender / AV / certs | `WindowsDefenderService`, `AntivirusExclusionHelper`, `ReFixCertificateHelper` | Not registered / no-op |
| App updater | `GitHubUpdateService.cs:321-331,478-526` | Assets `-linux-x64.AppImage` / `.tar.gz`; replace `$APPIMAGE` (not ProcessPath) and restart; tarball swap via `sh` |
| Localization | `Themes/Strings.*.xaml`, `LocalizationService.cs:82` | Convert to a UI-neutral format (resx/JSON) during Presentation extraction |
| WebView | `SteamWebSessionHelper.cs:35,112-115` cookies; `NexusBrowserPanel.xaml.cs:152,211`, `InstanceDetailView.xaml.cs:289` nav cancel + scripts | Must be proven in the phase-0 spike; fallback "open in browser" |
| Paths | 49 backslash literals, 35 `File.Exists(Path.Combine(...))` | `GameFileLocator.FindFile(dir, name)` case-insensitive, used by ReFix/BepInEx/engine/mods |

## 2. Compatibility runtime

`Infrastructure/Compat/` (registered only on Linux):
- `CompatRunnerLocator`: `umu-run` on PATH or BlueStar's pinned copy (`$XDG_DATA_HOME/BlueStar/runtimes/umu`, checksum verified); Proton dirs in every Steam library `steamapps/common/Proton*` and `<steam>/compatibilitytools.d/*`.
- `CompatEnvironmentBuilder.Build(settings, instance, detectedLoaders)` (pure, unit tested): `WINEPREFIX`, `PROTONPATH`, `GAMEID=umu-<appid|0>`, `STEAM_COMPAT_*` for ProtonDirect, `WINEDLLOVERRIDES` = auto (`winhttp` BepInEx, `winmm` ReFix Online loader, `version`, `dinput8`) + user extras, user extra env vars.
- `CompatLauncher`: Umu / ProtonDirect / ThroughSteam; Stop via `wineserver -k`; running state by watching the prefix's wineserver (also used for ThroughSteam playtime).
- ThroughSteam: shortcut via existing shortcuts.vdf writer with `LaunchOptions = WINEDLLOVERRIDES="..." %command%` + CompatToolMapping in `config.vdf` (Steam closed, backup first). For Steam-owned games launched by Steam: show the launch option with a copy button.
- Prefix: per instance, `$XDG_DATA_HOME/BlueStar/prefixes/<instanceId>`, path saved on the instance.
- Health check: detects AppArmor `kernel.apparmor_restrict_unprivileged_userns=1` / bwrap failure and falls back to ProtonDirect automatically, telling the user.

**Settings → "Compatibilidad (Wine/Proton)"** (only when `OperatingSystem.IsLinux()`): launch strategy, runner dropdown (discovered + "UMU-Proton (auto)" + "GE-Proton (latest)", which umu fetches itself), extra env vars, extra DLL overrides, buttons Open winecfg / Run winetricks / Delete prefix, runtime health. Same three fields overridable per instance. Model: `CompatSettings` on `AppSettings`, `CompatSettings? CompatOverride` on `GameInstance`.

## 3. Distribution and requirements

- Self-contained `linux-x64` publish → AppImage (static runtime, no libfuse2) + tar.gz. Flatpak later.
- Startup requirements page: glibc ≥ 2.31, ICU (or invariant globalization), WebKitGTK 4.1, Vulkan, python3 (umu), 32-bit glibc (SteamCMD), userns status.
- `build-release.ps1 -Linux`; `.github/workflows/linux.yml` with `workflow_dispatch` artifact upload (AppImage for a Steam Deck tester).

## 4. Testing (dev PC: Windows 11, no WSL/Docker)

1. CI `ubuntu-latest`: `dotnet test` for Core + Infrastructure tests (Windows-only tests tagged `[Trait("OS","Windows")]` and skipped on Linux so CI starts green; `BlueStar.App.Tests` stays Windows-only), Avalonia.Headless tests for the new head, `shellcheck` + `bats` on AutoDeploy.sh against fake game trees (Unity, Unreal, x86), umu job with `sysctl kernel.apparmor_restrict_unprivileged_userns=0` running a tiny test .exe and asserting env + exit code, ProtonDirect job.
2. Local without Linux: shellcheck (Windows build) + bats under Git Bash; unit tests with fake filesystem trees for locator, env builder, vdf writers, registry.vdf parser, case-insensitive locator.
3. Optional local Linux: WSL2/WSLg or Hyper-V VM (needs admin + reboot: user's call).
4. Real hardware checklist `docs/linux-testing.md` (Steam Deck Desktop + Game Mode, Fedora/Arch with Steam, a Steam-owned DRM game with and without ReFix, `--launch` from a Steam shortcut in Game Mode) before beta.

## 5. Phases

0. Retarget net10.0 (all projects + tests green on Windows). Avalonia 12 spike on Linux/Steam Deck: window in gamescope, WebView cookies + navigation cancel + script exec on WebKitGTK, AppImage starts. Go/no-go.
1. Linux CI green: test tagging, `GameFileLocator`, Steam path/registry.vdf, `AddBlueStar()` DI extraction, `--launch` arg, activation via pipe.
2. Compat runtime (section 2) + ReFix via AutoDeploy.sh (ini placement in C#) + BepInEx overrides + ISecureStorage/IWin32Linker Linux impls + SteamCMD Linux + nxm handler + updater chmod.
3. Presentation extraction one ViewModel at a time (Settings, Library, InstanceDetail first), localization to neutral format; WPF keeps working on top.
4. Avalonia head **Linux beta**: shell, Library, InstanceDetail (launch, ReFix, mods), Settings + Compat section; Home/Browse open in browser until WebView passes. Ships with packaging, Linux updater, requirements page, docs.
5. Remaining screens (Home, Browse, mod browsers) to parity; then retire WPF.

## 6. Risks

- Avalonia port size: mitigated by phase-0 spike, per-ViewModel extraction, early beta scope.
- umu/pressure-vessel blocked by distro policy: auto fallback to ProtonDirect.
- ReFix .sh contract: placement fixed in C# after the script runs, so ReFix updates cannot break it; upstream PR optional.
- WebKitGTK API gaps: spike decides; fallback is the system browser.
- Build note: AutoDeploy.sh also puts the Fusion `ReFix/` folder and the `winmm` cleanup in the folder it was given; on Unreal (exe in `Binaries/Win64`) check they end up next to the exe like `ReFix.ini`.

Review: architect subagent, 3 rounds, final verdict APPROVE.

## 7. Status (2026-11-14)

- Phase 0 spike result (Avalonia.Controls.WebView 12.1.2 on WebKitGTK 2.52, CI xvfb): script exec PASS, navigation cancel PASS, **cookies FAIL** (the GTK adapter's cookie manager needs WebKitGTK 6.0's network-session API). Decision: Steam login and other HttpOnly-cookie flows go through the system browser. Spike project removed after the decision.
- Phases 0–2 done. Phase 4 started as a separate, lean Avalonia head (`src/BlueStar.Linux`): Library (folder/Steam import, Play/Stop, ReFix), Settings → Compatibility, Requirements. Shared, UI-free services (`GameImportService`, `ReFixInstanceService`) are used by both heads; the rest of the WPF ViewModels are not shared yet (phase 3).
- Packaging: `packaging/linux/build-linux.sh` (AppImage + tar.gz). CI job `linux-app` runs a real Windows .exe through BlueStar + umu + UMU-Proton.
