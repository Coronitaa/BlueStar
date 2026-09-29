====================================================================
               ReFix Deployment Suite v2.0-pre (EOS Online v3)
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
- Restores original backup DLLs (.orig, _valve.dll, _o.dll, .steamstub.exe)
  and removes all proxies, emulators, and caches with zero leftover trace.

====================================================================
               NEW IN v2.0-pre (EOS Online v3)
====================================================================

ONLINE MULTIPLAYER (WAN)
  * Real Steam lobbies over Spacewar (AppID 480) for WAN relay
  * SteamNetworkingMessages relay handles NAT traversal
  * Interoperable by AppID, not by ReFix-specific keys

CLOUD SAVE
  * Local save files mirrored to EOS cloud storage slot
  * Fixes "CloudSave: Error" in Unreal Engine titles

FRIENDS LIST
  * Steam friends appear with correct names in game UI
  * ExternalAccountInfo display name populated from Steam persona

INVITATIONS
  * Send Steam invites + configurable chat message from in-game
  * ISteamFriends vtable hooks on correct slots (33/49)
  * PUID-to-SteamID resolution for friends not already in a lobby

WORKSHOP MOD FILTERING
  * Hooks correct ISteamUGC version (v016-v020 vtable support)
  * Filters subscribed items by RequireFilePattern per game
  * IncludeItems / ExcludeItems override lists

DEBUG
  * LogFriendsApi option for ISteamFriends call tracing
  * Instance tag for local two-player testing on same PC

====================================================================

Quick Start:
  Simply launch AutoDeploy.bat, select your game directory,
  and follow the interactive prompts!

NOTE: This is a PRE-RELEASE. Some features (joining from invitation,
overlay invite dialog) are not yet fully verified.
