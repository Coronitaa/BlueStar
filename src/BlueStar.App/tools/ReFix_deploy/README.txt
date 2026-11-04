ReFix 2.1.0 - LAN / Online
========================

1. Run AutoDeploy.bat.
2. Pick the game folder.
3. Choose the mode:
   [1] ReFix Online via Steam  - uses the Steam client.
   [2] ReFix LAN without Steam - plays on the local network, Steam not needed.
4. Launch the game with its own .exe (or a shortcut to that .exe).
   steam:// shortcuts start the game through Steam, without ReFix LAN.

ReFix LAN
- Every PC (and every instance on one PC) gets its own identity automatically
  ([User] IdentityMode = auto in ReFix.ini). A patched game folder can be copied to
  another PC as is.
- Several instances of the same game can run on one PC.
- Players on the same network find each other automatically.

- In-game overlay (AutoDeploy asks, [Overlay] in ReFix.ini): the ReFix overlay (Shift+Tab, or the
  Xbox / PS button of a controller), Steam's own overlay, or none. Overlay sounds: [Overlay] Sounds.
- Debug logging is off ([Debug] EnableLog = false); turn it on only to report a problem.

Uninstall_ReFix.bat restores the game's original files and gives the game this PC's own saves back.
ReFix LAN profiles (and the saves each one keeps) stay in %APPDATA%\ReFix: every ReFix game of the PC
can use them ("Import from another game" when creating a profile).

Linux (Steam Play / Proton)
- Run: bash AutoDeploy.sh
  or:  bash AutoDeploy.sh "<game folder>" lan|online [appid]
- ReFix Online: start the game from Steam. ReFix LAN: start the .exe with Proton/Wine
  or add it to Steam as a non-Steam game.
- Undo: bash AutoDeploy.sh --uninstall "<game folder>"
