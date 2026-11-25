## BlueStar v1.6.5

### Bug Fixes & Emulator Reliability
* **Critical ReFix Uninstaller Fix:** Fixed a severe issue where uninstalling or switching emulators/versions could recursively delete the game directory when `.refix_installed` was placed inside the game's executable folder (`rd /s /q` replaced with non-recursive `rd`).
* **Runtime Uninstaller Guard:** Added `SanitizeUninstallerScript` in `ReFixEmulator` and `ReFixUpdateService` so previously downloaded or newly fetched ReFix suites (including v2.6.2) are sanitized before execution.
* **Updated Internal ReFix Scripts:** Updated bundled `AutoDeploy.bat`, `AutoDeploy.sh`, and `Uninstall_ReFix.bat` to ReFix v2.6.2, with Steam shortcut auto-copy/prompt scripts removed from `AutoDeploy.bat` since BlueStar manages launches directly.

### UI, Accessibility & Polish
* **Contrast & Keyboard Focus:** Improved `MutedTextBrush` (`#8E8E9E`) to meet WCAG AA contrast on dark card and input surfaces, and restored the keyboard focus visual on the Browse random-filter button.
* **Copy & Visual Cleanup:** Replaced em dashes across English and Spanish localization strings, removed generic decorative emojis from action labels, and trimmed redundant card-badge drop shadows.

### Native ARM64 & Linux Preparations (since v1.6.4)
* **Native Windows ARM64 Support:** Native `win-arm64` builds for BlueStar UI/host while keeping game depots and ReFix hooks on x86/x64 via Windows 11 Prism emulation.
* **Architecture-Aware Depots & Updates:** `DepotArchitectureHelper` and `GitHubUpdateService` strictly validate and match host architecture (`x64` vs `arm64`) when filtering depots and fetching updates.
* **ARM64 Prerequisites & Linux AArch64:** Integrated `vc_redist.arm64.exe` detection/installation and updated `UnixLinker` and Linux packaging scripts (`build-linux.ps1`, `pack_wsl.sh`) for `1.6.5`.

---
**Full Changelog**: https://github.com/Coronitaa/BlueStar/compare/v1.6.4...v1.6.5
