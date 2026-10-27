====================================================================
               ReFix Deployment Suite v1.4.1 (Standalone)
====================================================================

WHAT'S NEW IN v1.4.1 (test build):
- Photon P2P: lower voice/game latency. The emulator no longer waits up to ~15 ms per Steam hop, enables
  direct Steam P2P routes (LAN / nearby players skip the Valve relay) and raises Steam's send-rate cap.
- Photon P2P: joins survive an unstable host better (longer Steam session timeouts, session opened early).
- ReFix.log now shows per player: RELAYED/direct route, ping, quality and send queue ('Steam session' lines).
- New optional [Photon] keys: DirectConnections, SendRateMinKB, SendRateMaxKB, SessionConnectTimeoutMs,
  SessionTimeoutMs.

WHAT'S NEW IN v1.4.0:
- Local Photon Cloud emulator (ReFixCloud): Photon games (PUN 2, Realtime, Voice and Fusion) run with no
  Photon servers. NameServer, Master and GameServer are emulated on each PC and game traffic travels over
  Steam P2P (Spacewar 480). Fusion's server-plugin handshake (Join / Start) is emulated too.
- Smarter auto-decision: AutoDeploy detects Photon (including IL2CPP builds via global-metadata.dat) and
  writes Backend=ReFixCloud automatically; the emulator stays inert unless the game really contacts Photon.
- Generic Steam P2P / SDR tunnelling for serverless P2P games, and opt-in offline inventory persistence
  (ReFix_Inventory.json, [Inventory] EnableLocalInventory). Purchases are never emulated.
- Version-aware Steam interface hooks: every C++ vtable hook is matched to the interface version the game
  uses (fixes crashes and broken auth/invites on Steamworks 1.57-1.61 games such as Unreal 5 titles).
- Game-specific helpers are opt-in per game in ReFix.ini (HTTP proxy, local backend, fast cloud-fail,
  inventory); online games keep full access to EOS, PlayFab and other official services.
- Photon emulator joins now work across PCs when the game swaps Photon's GameServer port (e.g. R.E.P.O.).
- Photon Fusion host/client games (e.g. Roadside Research) can now be joined from another PC: the emulator
  plays the parts of Photon's Fusion plugin that clients need (relay address, player UniqueId and player
  mapping), and large relayed packets are reassembled correctly.
- One clean backup: Online mode keeps a single steam_api64_original.dll next to the proxy (no duplicate
  _valve copy), and Uninstall_ReFix.bat restores it first. Re:Goldberg deployments verify correctly again.
- 32-bit games (e.g. Overcooked! 2): the x86 proxy now hands games the exact Steam interface version they
  ask for (fixes a startup crash), and AutoDeploy no longer drops proxies into Unity's Linux plugin folders.
- Clearer diagnostics: ReFix.log warns when another program has hijacked Steam's 32-bit client path
  (restart Steam to fix it) and reports the real error when a local port cannot be opened.
- Generic IL2CPP bridge: license hotpatch, loopback preservation and fast cloud-failure fallback.
- AI Bridge and proxy performance work: optimized hot loops and SteamNetworkingSockets v012 support.
- Stability: fixed a crash on Steamworks 1.60 games caused by an ISteamFriends interface-version mismatch;
  all ISteamFriends access now runs on the game's main thread.

Carried over from v1.3.3:
- Universal AutoDeploy Path Robustness: Fixed special character loss ('!', '&', '()', '[]', '^', '%', and Unicode directory paths).
- Physical Post-Deployment Verification: Every critical file is verified on disk with strict exit code failure reporting.
- Steamworks SDK 1.60 Full Export Coverage: Added forwarders for modern Steamworks interfaces (1,141 exports total) fixing initialization crashes.
- Nested Executable Configuration Discovery: Automatic ancestor directory lookup for ReFix.ini and configuration files.
- Full Online Compatibility: Preserves seamless SteamNetworkingSockets (SDR), Facepunch, EOS, Mirror, Godot, and Unity networking.


INCLUDED TOOLS:

1. AutoDeploy.bat
-----------------
- Main automated Steam emulation and multiplayer deployment tool.
- Available modes:
  [1] ReFix Online via Steam (Spacewar 480 / official Steam infrastructure).
  [2] Re:Goldberg LAN without Steam (100% offline emulation, local subnet broadcast).
- Automatic engine detection (Unity Mono/IL2CPP, Unreal Engine 4/5, Godot 3/4, Native C/C++).
- Modular Epic Online Services (EOS v3 / RedboneEOS) support for Unreal and EOS titles.
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
