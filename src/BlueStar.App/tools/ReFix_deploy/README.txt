ReFix 2.4.3 - LAN / Online
========================

1. Run AutoDeploy.bat.
2. Pick the game folder.
3. Choose the mode:
   [1] ReFix Online + LAN            - plays on the local network, Steam not needed. With Steam installed,
       the Steam button in the overlay's top bar adds your Steam friends to the same rooms (optional).
   [2] ReFix Offline LAN             - the LAN version of the previous release (2.3.2): local network only.
   [3] ReFix Online Legacy via Steam - the Online version of the previous release (2.3.2): uses the Steam client.
4. Launch the game with its own .exe (or a shortcut to that .exe).
   steam:// shortcuts start the game through Steam, without ReFix LAN.

ReFix LAN
- Every PC (and every instance on one PC) gets its own identity automatically
  ([User] IdentityMode = auto in ReFix.ini). A patched game folder can be copied to
  another PC as is.
- Several instances of the same game can run on one PC.
- Players on the same network find each other automatically.

- In-game overlay (AutoDeploy asks, [Overlay] in ReFix.ini): the ReFix overlay (Shift+Tab, or the
  Xbox / PS button of a controller), Steam's own overlay, or none. The gear in the overlay opens its settings
  (notifications, sound, language, performance, profile sync).
- Profile sync (Settings > Syncing): keep each profile's saves, achievements and items in your Google Drive or
  OneDrive account, a cloud app's folder or a USB drive, or export / import a .rfxp file. Optional; your
  profiles always stay on this PC too. Privacy policy: https://coronitaa.github.io/privacy.html
- Debug logging is off ([Debug] EnableLog = false); turn it on only to report a problem.

Game mods: when refix_mods\catalog.tsv has a mod for the game, AutoDeploy installs it into the
game's refix_mods folder (CS:GO 1.37 has one). The uninstaller removes it again.

Uninstall_ReFix.bat (Windows) or Uninstall_ReFix.sh (Linux) restores the game's original files and gives the game this PC's own saves back.
ReFix LAN profiles (and the saves each one keeps) stay in %APPDATA%\ReFix: every ReFix game of the PC
can use them ("Import from another game" when creating a profile).

Linux (Steam Play / Proton)
- Run: bash AutoDeploy.sh
  or:  bash AutoDeploy.sh "<game folder>" lan|online [appid]
- ReFix Online: start the game from Steam. ReFix LAN: start the .exe with Proton/Wine
  or add it to Steam as a non-Steam game.
- Undo: bash Uninstall_ReFix.sh "<game folder>" (or bash AutoDeploy.sh --uninstall "<game folder>")
