@echo off
setlocal enabledelayedexpansion
title ReFix - Universal AutoDeploy Tool v1.1.0

set "SCRIPT_DIR=%~dp0"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"
set "BIN_DIR=!SCRIPT_DIR!\bin"

echo ====================================================================
echo                   ReFix Universal AutoDeploy Tool
echo ====================================================================
echo:

if not exist "!BIN_DIR!\steam_api64.dll" (
    echo [ERROR] Required binaries not found in "!BIN_DIR!"!
    echo Please make sure the 'bin' folder contains steam_api64.dll, etc.
    goto ERROR_EXIT
)

if not "%~1"=="" (
    set "TARGET_DIR=%~1"
    goto GOT_TARGET_DIR
)

echo Select deployment target mode:
echo [1] Deploy to game in CURRENT folder (!SCRIPT_DIR!)
echo [2] Select game folder using File Explorer (GUI Picker)
echo [3] Enter game folder path manually
echo:
set "TARGET_CHOICE=2"
set /p "TARGET_CHOICE=Select option [1-3] (Default: 2): "
if "!TARGET_CHOICE!"=="" set "TARGET_CHOICE=2"

if "!TARGET_CHOICE!"=="1" (
    set "TARGET_DIR=!SCRIPT_DIR!"
    goto GOT_TARGET_DIR
)
if "!TARGET_CHOICE!"=="3" goto MANUAL_TARGET_INPUT

:: Option 2: GUI Picker
echo:
echo [INFO] Opening File Explorer folder selection dialog...
if exist "!BIN_DIR!\select_folder.ps1" (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\select_folder.ps1" ^<nul`) do (
        set "TARGET_DIR=%%I"
    )
)
goto GOT_TARGET_DIR

:MANUAL_TARGET_INPUT
echo:
set /p "TARGET_DIR=Enter target game directory path: "
goto GOT_TARGET_DIR

:GOT_TARGET_DIR
if "!TARGET_DIR!"=="" (
    echo:
    echo [NOTICE] No folder was selected or operation was cancelled by user.
    goto ERROR_EXIT
)

:: Strip trailing backslash and quotes if present
set "TARGET_DIR=!TARGET_DIR:"=!"
if "!TARGET_DIR:~-1!"=="\" set "TARGET_DIR=!TARGET_DIR:~0,-1!"

if not exist "!TARGET_DIR!" (
    echo:
    echo [ERROR] Directory does not exist: "!TARGET_DIR!"
    goto ERROR_EXIT
)

echo:
echo ====================================================================
echo Analyzing Game Directory: "!TARGET_DIR!"
echo ====================================================================
echo:

:: Run smart game detector to find exact EXE_DIR, GAME_EXE_PATH, ENGINE_TYPE, GAME_NAME, and DETECTED_APPID
set "ENGINE_TYPE=Native"
set "EXE_DIR=!TARGET_DIR!"
set "GAME_EXE_PATH="
set "DEFAULT_GAME_NAME="
set "DETECTED_APPID="
set "CANDIDATE_EXES="

if exist "!BIN_DIR!\detect_game.ps1" (
    for /f "usebackq tokens=1,* delims==" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\detect_game.ps1" -TargetDir "!TARGET_DIR!" ^<nul`) do (
        if /i "%%A"=="ENGINE_TYPE" set "ENGINE_TYPE=%%B"
        if /i "%%A"=="EXE_DIR" set "EXE_DIR=%%B"
        if /i "%%A"=="GAME_EXE_PATH" set "GAME_EXE_PATH=%%B"
        if /i "%%A"=="GAME_NAME" set "DEFAULT_GAME_NAME=%%B"
        if /i "%%A"=="DETECTED_APPID" set "DETECTED_APPID=%%B"
        if /i "%%A"=="CANDIDATE_EXES" set "CANDIDATE_EXES=%%B"
    )
)

if "!DEFAULT_GAME_NAME!"=="" (
    for %%I in ("!TARGET_DIR!") do set "DEFAULT_GAME_NAME=%%~nxI"
)

echo [DETECTION] Detected Engine Type: !ENGINE_TYPE!
echo [DETECTION] Executable Location: "!EXE_DIR!"
if not "!GAME_EXE_PATH!"=="" echo [DETECTION] Game Executable: "!GAME_EXE_PATH!"
if not "!DETECTED_APPID!"=="" echo [DETECTION] Detected Steam AppID: !DETECTED_APPID!
echo:

:: Executable Location and Selection Prompt
echo Confirm or Change Executable Location:
echo [1] Use detected executable and location (Default)
echo [2] Select game executable using File Explorer (GUI Picker)
echo [3] Enter game executable path manually (.exe)
echo [4] Enter executable directory path manually (folder)
echo:
set "EXE_CHOICE=1"
set /p "EXE_CHOICE=Select option [1-4] (Default: 1): "
if "!EXE_CHOICE!"=="" set "EXE_CHOICE=1"

if "!EXE_CHOICE!"=="2" (
    echo:
    echo [INFO] Opening File Explorer to select executable...
    if exist "!BIN_DIR!\select_exe.ps1" (
        for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\select_exe.ps1" -InitialDir "!EXE_DIR!" ^<nul`) do (
            set "USER_EXE=%%I"
        )
        if not "!USER_EXE!"=="" (
            set "GAME_EXE_PATH=!USER_EXE!"
            for %%F in ("!USER_EXE!") do set "EXE_DIR=%%~dpF"
            if "!EXE_DIR:~-1!"=="\" set "EXE_DIR=!EXE_DIR:~0,-1!"
            for %%F in ("!USER_EXE!") do set "DEFAULT_GAME_NAME=%%~nF"
        )
    )
)
if "!EXE_CHOICE!"=="3" (
    echo:
    set /p "MANUAL_EXE_PATH=Enter full executable file path (.exe): "
    if not "!MANUAL_EXE_PATH!"=="" (
        set "MANUAL_EXE_PATH=!MANUAL_EXE_PATH:"=!"
        set "GAME_EXE_PATH=!MANUAL_EXE_PATH!"
        for %%F in ("!MANUAL_EXE_PATH!") do set "EXE_DIR=%%~dpF"
        if "!EXE_DIR:~-1!"=="\" set "EXE_DIR=!EXE_DIR:~0,-1!"
        for %%F in ("!MANUAL_EXE_PATH!") do set "DEFAULT_GAME_NAME=%%~nF"
    )
)
if "!EXE_CHOICE!"=="4" (
    echo:
    set /p "MANUAL_EXE_DIR=Enter executable directory path: "
    if not "!MANUAL_EXE_DIR!"=="" (
        set "MANUAL_EXE_DIR=!MANUAL_EXE_DIR:"=!"
        if "!MANUAL_EXE_DIR:~-1!"=="\" set "MANUAL_EXE_DIR=!MANUAL_EXE_DIR:~0,-1!"
        set "EXE_DIR=!MANUAL_EXE_DIR!"
    )
)

echo:
set "USER_ENGINE_CHOICE="
set /p "USER_ENGINE_CHOICE=Confirm Engine [Unity/Godot/Unreal/Native] (Default: !ENGINE_TYPE!): "
if not "!USER_ENGINE_CHOICE!"=="" set "ENGINE_TYPE=!USER_ENGINE_CHOICE!"

:: Read existing settings from ReFix.ini if it already exists in target
set "EXISTING_REAL_APPID=!DETECTED_APPID!"
set "EXISTING_USERNAME="
set "EXISTING_STEAMID="
set "EXISTING_LAN_PORT=47584"
if exist "!EXE_DIR!\ReFix.ini" (
    for /f "usebackq tokens=1,* delims==" %%A in ("!EXE_DIR!\ReFix.ini") do (
        if /i "%%A"=="RealAppId" (
            if not "%%B"=="" if not "%%B"=="0" if not "%%B"=="480" set "EXISTING_REAL_APPID=%%B"
        )
        if /i "%%A"=="Name" set "EXISTING_USERNAME=%%B"
        if /i "%%A"=="SteamId" set "EXISTING_STEAMID=%%B"
        if /i "%%A"=="ListenPort" set "EXISTING_LAN_PORT=%%B"
    )
)
if "!EXISTING_REAL_APPID!"=="" set "EXISTING_REAL_APPID=480"

set "GAME_NAME="
set /p "GAME_NAME=Enter Game Name (Default: !DEFAULT_GAME_NAME!): "
if "!GAME_NAME!"=="" set "GAME_NAME=!DEFAULT_GAME_NAME!"

echo:
echo ====================================================================
echo                  Select Connectivity Mode
echo ====================================================================
echo:
echo  [1] ReFix Online via Steam
echo      - Uses Steam client and Spacewar (AppID 480).
echo      - Enables online multiplayer matchmaking via Steam servers.
echo:
echo  [2] Re:Goldberg LAN without Steam
echo      - 100%% autonomous local emulation based on gbe_fork backend.
echo      - Allows playing on LAN without requiring Steam installed or running.
echo      - Local broadcast discovery, portable saves, and persistent identity.
echo:
set "ONLINE_MODE_CHOICE="
set /p "ONLINE_MODE_CHOICE=Select Option [1-2] (Default: 1): "
if "!ONLINE_MODE_CHOICE!"=="" set "ONLINE_MODE_CHOICE=1"

set "ONLINE_MODE_NAME=valve"
if "!ONLINE_MODE_CHOICE!"=="2" set "ONLINE_MODE_NAME=goldberg"

if "!ONLINE_MODE_NAME!"=="goldberg" goto GOLDBERG_PROMPTS

:VALVE_PROMPTS
echo:
echo [INFO] Selected Mode: ReFix Online via Steam
echo:
set "MASK_APPID=480"
set "REAL_APPID="
if not "!EXISTING_REAL_APPID!"=="" if not "!EXISTING_REAL_APPID!"=="480" (
    set /p "REAL_APPID=Enter Real Steam AppID (Default: !EXISTING_REAL_APPID!): "
    if "!REAL_APPID!"=="" set "REAL_APPID=!EXISTING_REAL_APPID!"
) else (
    set /p "REAL_APPID=Enter Real Steam AppID: "
    if "!REAL_APPID!"=="" set "REAL_APPID=480"
)

set "CUSTOM_USERNAME="
set /p "CUSTOM_USERNAME=Enter Custom Username (Optional - Enter to use Steam name): "
if "!CUSTOM_USERNAME!"=="" set "CUSTOM_USERNAME=!EXISTING_USERNAME!"
set "LAN_PORT=47584"
set "CUSTOM_BROADCASTS="
goto AFTER_MODE_PROMPTS

:GOLDBERG_PROMPTS
echo:
echo [INFO] Selected Mode: Re:Goldberg LAN without Steam
echo:
set "CUSTOM_USERNAME="
set /p "CUSTOM_USERNAME=Enter Player Username (Leave empty to auto-generate): "
if "!CUSTOM_USERNAME!"=="" set "CUSTOM_USERNAME=!EXISTING_USERNAME!"

set "REAL_APPID="
if not "!EXISTING_REAL_APPID!"=="" if not "!EXISTING_REAL_APPID!"=="480" (
    set /p "REAL_APPID=Enter Real Steam AppID (Default: !EXISTING_REAL_APPID!): "
    if "!REAL_APPID!"=="" set "REAL_APPID=!EXISTING_REAL_APPID!"
) else (
    set /p "REAL_APPID=Enter Real Steam AppID: "
    if "!REAL_APPID!"=="" set "REAL_APPID=480"
)
set "MASK_APPID=!REAL_APPID!"

set "LAN_PORT="
set /p "LAN_PORT=Enter LAN Listen Port (Default: !EXISTING_LAN_PORT!): "
if "!LAN_PORT!"=="" set "LAN_PORT=!EXISTING_LAN_PORT!"

set "CUSTOM_BROADCASTS="
set /p "CUSTOM_BROADCASTS=Enter Custom Broadcast IPs [Comma-separated, Optional]: "
goto AFTER_MODE_PROMPTS

:AFTER_MODE_PROMPTS
set "PHOTON_APPID="
set "PHOTON_REGION="
set "CUSTOM_PUBLIC_IP="
set "FIREWALL_AUTO_APPLY=false"

:: ====================================================================
:: DLC Selection Prompt (All, None, or Custom)
:: ====================================================================
echo:
echo ====================================================================
echo               DLC Unlock Configuration
echo ====================================================================
echo:
echo  [1] Unlock ALL DLCs (Universal auto-unlock mode)
echo  [2] Unlock NO DLCs (Lock all DLCs)
echo  [3] Choose specific DLCs (Steam Store catalog or manual IDs)
echo:
set "DLC_CHOICE="
set /p "DLC_CHOICE=Select Option [1-3] (Default: 1): "
if "!DLC_CHOICE!"=="" set "DLC_CHOICE=1"

set "DLC_MODE=all"
set "DLCS=all"

if "!DLC_CHOICE!"=="2" (
    set "DLC_MODE=none"
    set "DLCS=none"
)
if "!DLC_CHOICE!"=="3" (
    set "DLC_MODE=custom"
    set "TMP_DLC_OUT=%TEMP%\refix_dlc_sel_!RANDOM!.txt"
    if exist "!BIN_DIR!\select_dlcs.ps1" (
        powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\select_dlcs.ps1" -AppId "!REAL_APPID!" -GameName "!GAME_NAME!" -OutputFile "!TMP_DLC_OUT!"
        if exist "!TMP_DLC_OUT!" (
            set /a LINE_COUNT=0
            for /f "usebackq delims=" %%L in ("!TMP_DLC_OUT!") do (
                set /a LINE_COUNT+=1
                if !LINE_COUNT!==1 set "DLC_MODE=%%L"
                if !LINE_COUNT!==2 set "DLCS=%%L"
            )
            del "!TMP_DLC_OUT!" >nul 2>&1
        )
    ) else (
        set /p "DLCS=Enter DLC AppIDs [Comma-separated, e.g. 12345,67890]: "
    )
)

set "BYPASS_LICENSE=true"
if "!DLC_MODE!"=="none" set "BYPASS_LICENSE=false"

if "!ONLINE_MODE_NAME!"=="goldberg" (
    echo:
    echo ====================================================================
    echo             Windows Firewall Configuration for LAN
    echo ====================================================================
    echo:
    echo  Do you want to automatically authorize the game and UDP port !LAN_PORT!
    echo  in Windows Firewall to ensure LAN discovery?
    echo:
    set "FW_CHOICE="
    set /p "FW_CHOICE=Apply Firewall rules? [Y/N] (Default: Y): "
    if "!FW_CHOICE!"=="" set "FW_CHOICE=Y"
    if /i "!FW_CHOICE!"=="N" (
        set "FIREWALL_AUTO_APPLY=false"
    ) else (
        set "FIREWALL_AUTO_APPLY=true"
    )
)

echo:
echo ====================================================================
echo Starting ReFix Deployment... (Mode: !ONLINE_MODE_NAME!)
echo ====================================================================

:: Step 1: Copy winmm.dll proxy only if in Valve mode
if "!ONLINE_MODE_NAME!"=="goldberg" goto SKIP_WINMM_DEPLOY
if not exist "!BIN_DIR!\winmm.dll" goto SKIP_WINMM_DEPLOY

echo [1/6] Deploying winmm.dll proxy...
copy /y "!BIN_DIR!\winmm.dll" "!EXE_DIR!\winmm.dll" >nul
echo [OK] Deployed winmm.dll to "!EXE_DIR!"
goto AFTER_WINMM_DEPLOY

:SKIP_WINMM_DEPLOY
echo [1/6] Skipping winmm.dll proxy for offline/native mode...

:AFTER_WINMM_DEPLOY

:: Steps 2 & 3: Run PowerShell helper for deployment & configuration synchronization
if exist "!BIN_DIR!\deploy_helper.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\deploy_helper.ps1" -TargetDir "!TARGET_DIR!" -BinDir "!BIN_DIR!" -ExeDir "!EXE_DIR!" -EngineType "!ENGINE_TYPE!" -OnlineMode "!ONLINE_MODE_NAME!" -GameName "!GAME_NAME!" -UserName "!CUSTOM_USERNAME!" -RealAppId "!REAL_APPID!" -MaskAppId "!MASK_APPID!" -Language "english" -DLCs "!DLCS!" -DLCMode "!DLC_MODE!" -ListenPort "!LAN_PORT!" -CustomBroadcasts "!CUSTOM_BROADCASTS!" -PhotonAppId "!PHOTON_APPID!" -PhotonRegion "!PHOTON_REGION!"
) else (
    echo [ERROR] !BIN_DIR!\deploy_helper.ps1 missing!
    goto ERROR_EXIT
)

:: Step 4 & 5: For Valve mode only, write additional legacy ReFix.ini and steam_appid.txt
if "!ONLINE_MODE_NAME!"=="goldberg" goto SKIP_VALVE_MANUAL_INI

echo [4/6] Generating steam_appid.txt...
echo !MASK_APPID!> "!EXE_DIR!\steam_appid.txt"
echo [OK] Created steam_appid.txt (AppID: !MASK_APPID!)

echo [5/6] Writing ReFix.ini configuration...
set "GAME_FILTER_VALUE="
if not "!REAL_APPID!"=="" set "GAME_FILTER_VALUE=!REAL_APPID!"

(
echo [Game]
echo GameName=!GAME_NAME!
echo EngineType=!ENGINE_TYPE!
echo.
echo [Matchmaking]
echo EnableLobbyFilter=false
echo LobbyFilterKey=game_filter
echo LobbyFilterValue=!GAME_FILTER_VALUE!
echo LobbyDistanceFilter=Worldwide
echo MaxLobbyResults=50
echo.
echo [ServerBrowser]
echo OverrideServerListAppId=false
echo ServerListAppId=480
echo Language=english
echo.
echo [Steam]
echo MaskAppId=!MASK_APPID!
echo RealAppId=!REAL_APPID!
echo Language=english
echo BypassLicenseCheck=!BYPASS_LICENSE!
echo DLCs=!DLCS!
echo.
echo [Overlay]
echo EnableOverlay=true
echo OverlayAppId=!MASK_APPID!
echo.
echo [EOS]
echo DeviceIdAuth=true
echo.
echo [User]
echo Name=!CUSTOM_USERNAME!
echo SteamId=
echo.
echo [Online]
echo Mode=!ONLINE_MODE_NAME!
echo.
echo [Network]
echo GameFilter=!GAME_FILTER_VALUE!
echo PublicIP=!CUSTOM_PUBLIC_IP!
echo LocalIP=
echo.
echo [P2P]
echo EnableWAN=true
echo P2PPort=7777
echo AllowRelay=true
echo ForcePublicIPInLobby=true
) > "!EXE_DIR!\ReFix.ini"
echo [OK] Configured ReFix.ini in "!EXE_DIR!"

:SKIP_VALVE_MANUAL_INI

:: Step 6: Copy Steam Shortcut Installer & PowerShell helper (Valve mode only)
if "!ONLINE_MODE_NAME!"=="valve" (
    echo [6/6] Copying Steam Shortcut scripts...
    if exist "!BIN_DIR!\add_steam_shortcut.ps1" (
        copy /y "!BIN_DIR!\add_steam_shortcut.ps1" "!EXE_DIR!\add_steam_shortcut.ps1" >nul
    )
    if exist "!BIN_DIR!\Install_ReFix_Steam_Shortcut.bat" (
        copy /y "!BIN_DIR!\Install_ReFix_Steam_Shortcut.bat" "!EXE_DIR!\Install_ReFix_Steam_Shortcut.bat" >nul
    )
    echo [OK] Deployed Steam Shortcut Installer scripts to "!EXE_DIR!"
) else (
    echo [6/6] Finalizing portable configuration...
)

:: ============================================================
:: Step 7: Apply Windows Firewall Rules dynamically
:: ============================================================
if "!FIREWALL_AUTO_APPLY!" neq "true" goto SKIP_FIREWALL

echo:
echo [7/7] Configuring Windows Firewall rules for game and LAN network...
if exist "!BIN_DIR!\apply_firewall.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\apply_firewall.ps1" -GameExe "!GAME_EXE_PATH!" -GameName "!GAME_NAME!" -LanPort "!LAN_PORT!" -Mode "!ONLINE_MODE_NAME!"
)

:SKIP_FIREWALL

echo:
echo ====================================================================
echo  DEPLOY COMPLETE
echo  Game: !GAME_NAME!  ^|  Engine: !ENGINE_TYPE!
if "!ONLINE_MODE_NAME!"=="goldberg" goto SUMMARY_GOLDBERG

:SUMMARY_VALVE
echo  Mode: ReFix Online via Steam [Spacewar 480]
echo  Location: "!EXE_DIR!"
echo  AppID: MaskAppId=!MASK_APPID!  /  RealAppId=!REAL_APPID!
echo  DLCs: Mode=!DLC_MODE! [!DLCS!]
goto AFTER_SUMMARY

:SUMMARY_GOLDBERG
echo  Mode: Re:Goldberg LAN without Steam [Offline / gbe_fork]
echo  Location: "!EXE_DIR!"
echo  AppID: RealAppId=!REAL_APPID!
echo  DLCs: Mode=!DLC_MODE! [!DLCS!]
echo  LAN Port: !LAN_PORT! [UDP Broadcast Discovery]
echo  Storage: Portable in saves\ [USB / Flash Drive Compatible]
echo  Firewall Helper: "!EXE_DIR!\Configure_LAN_Firewall.bat"
goto AFTER_SUMMARY

:AFTER_SUMMARY
echo ====================================================================
echo:

:: Offer one-click launch if game executable is found
if "!GAME_EXE_PATH!"=="" goto SKIP_LAUNCH_OFFER

echo  Executable ready: "!GAME_EXE_PATH!"
echo:
set "LAUNCH_NOW=Y"
set /p "LAUNCH_NOW=Launch game now? [Y/N] (Default: Y): "
if /i "!LAUNCH_NOW!"=="N" goto SKIP_LAUNCH_OFFER

echo:
echo [LAUNCH] Starting "!GAME_NAME!"...
start "" "!GAME_EXE_PATH!"
echo [OK] Game started.
goto SUCCESS_EXIT

:SKIP_LAUNCH_OFFER

if "!ONLINE_MODE_NAME!"=="valve" (
    set "RUN_INSTALLER=Y"
    set /p "RUN_INSTALLER=Add to Steam as Non-Steam Game? [Y/N] (Default: Y): "
    if /i "!RUN_INSTALLER!"=="N" goto SUCCESS_EXIT

    echo:
    echo [INFO] Running Steam Shortcut Auto-Installer...
    pushd "!EXE_DIR!"
    if exist "Install_ReFix_Steam_Shortcut.bat" (
        call Install_ReFix_Steam_Shortcut.bat "!GAME_NAME!" "!GAME_EXE_PATH!"
    )
    popd
)

:SUCCESS_EXIT
echo:
echo Deployment completed successfully.
echo:
echo Press any key to exit...
pause >nul
exit /b 0

:ERROR_EXIT
echo:
echo [FATAL ERROR] The operation could not be completed. Check the messages above.
echo:
echo Press any key to exit...
pause >nul
exit /b 1
