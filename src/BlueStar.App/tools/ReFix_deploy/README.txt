====================================================================
               ReFix Deployment Suite v1.23 (Standalone)
====================================================================

INCLUDED TOOLS:

1. AutoDeploy.bat
-----------------
- Main automated Steam emulation and multiplayer deployment tool.
- Available modes:
  [1] ReFix Online via Steam (Spacewar 480 / official Steam infrastructure).
  [2] Re:Goldberg LAN without Steam (100% offline emulation, local subnet broadcast).
- Automatic engine detection (Unity, Unreal Engine 4/5, Godot 3/4, Native C/C++).
- Smart executable scoring (filters dedicated/server/nullrenderer exes) and GUI file picker.
- Generates portable 'Configure_LAN_Firewall.bat' helper for USB / flash drives.
- Integrated Steam shortcuts.vdf installer for Non-Steam Game library integration.

2. DLC_Unlocker.bat (BLUESTAR Engine)
--------------------------------------
- Universal DLC unlocker powered by SmokeAPI / CreamAPI.
- Three unlock modes:
  [1] Unlock ALL DLCs (universal auto-unlock).
  [2] Custom DLC selection (queried live from Steam Store API with titles & indices).
  [3] Unlock NO DLCs (clean parity testing mode).
- Non-destructive: Backs up original steam_api64.dll -> steam_api64_o.dll.
- Full install, uninstall, and status inspection commands.

3. Uninstall_ReFix.bat
----------------------
- Universal uninstaller and game restorer.
- Restores original backup DLLs (.orig, _valve.dll, _o.dll, .steamstub.exe) and removes all proxies, emulators, and caches with zero leftover trace.

4. Quick Start:
----------------------
- Simply launch AutoDeploy.bat or DLC_Unlocker.bat, select your game directory, and follow the interactive prompts!
