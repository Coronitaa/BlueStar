@echo off
chcp 65001 >nul
setlocal disabledelayedexpansion
title ReFix - Universal AutoDeploy Tool v1.3.3

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "BIN_DIR=%SCRIPT_DIR%\bin"

echo ====================================================================
echo                   ReFix Universal AutoDeploy Tool
echo ====================================================================
echo:

if not exist "%BIN_DIR%\steam_api64.dll" (
    echo [ERROR] Required binaries not found in "%BIN_DIR%"!
    echo Please make sure the 'bin' folder contains steam_api64.dll, etc.
    goto ERROR_EXIT
)

if not "%~1"=="" (
    set "TARGET_DIR=%~1"
    goto GOT_TARGET_DIR
)

echo Select deployment target mode:
echo [1] Deploy to game in CURRENT folder (%SCRIPT_DIR%)
echo [2] Select game folder using File Explorer (GUI Picker)
echo [3] Enter game folder path manually
echo:
set "TARGET_CHOICE=2"
set /p "TARGET_CHOICE=Select option [1-3] (Default: 2): "
if "%TARGET_CHOICE%"=="" set "TARGET_CHOICE=2"

if "%TARGET_CHOICE%"=="1" (
    set "TARGET_DIR=%SCRIPT_DIR%"
    goto GOT_TARGET_DIR
)
if "%TARGET_CHOICE%"=="3" goto MANUAL_TARGET_INPUT

:: Option 2: GUI Picker
echo:
echo [INFO] Opening File Explorer folder selection dialog...
if exist "%BIN_DIR%\select_folder.ps1" (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\select_folder.ps1" ^<nul`) do (
        set "TARGET_DIR=%%I"
    )
)
goto GOT_TARGET_DIR

:MANUAL_TARGET_INPUT
echo:
set /p "TARGET_DIR=Enter target game directory path: "
goto GOT_TARGET_DIR

:GOT_TARGET_DIR
if "%TARGET_DIR%"=="" (
    echo:
    echo [NOTICE] No folder was selected or operation was cancelled by user.
    goto ERROR_EXIT
)

:: Strip trailing backslash, forward slash, spaces and quotes if present
set "TARGET_DIR=%TARGET_DIR:"=%"
:TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"==" " set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"=="\" set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"=="/" set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR

if not exist "%TARGET_DIR%" (
    echo:
    echo [ERROR] Directory does not exist: "%TARGET_DIR%"
    goto ERROR_EXIT
)

echo:
echo ====================================================================
echo Analyzing Game Directory: "%TARGET_DIR%"
echo ====================================================================
echo:

:: Run smart game detector to find exact EXE_DIR, GAME_EXE_PATH, ENGINE_TYPE, GAME_NAME, and DETECTED_APPID
set "ENGINE_TYPE=Native"
set "EXE_DIR=%TARGET_DIR%"
set "GAME_EXE_PATH="
set "DEFAULT_GAME_NAME="
set "DETECTED_APPID="
set "CANDIDATE_EXES="

set "REFIX_TARGET_DIR=%TARGET_DIR%"
set "REFIX_BIN_DIR=%BIN_DIR%"

if exist "%BIN_DIR%\detect_game.ps1" (
    for /f "usebackq tokens=1,* delims==" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\detect_game.ps1" -TargetDir "%TARGET_DIR%" ^<nul`) do (
        if /i "%%A"=="ENGINE_TYPE" set "ENGINE_TYPE=%%B"
        if /i "%%A"=="EXE_DIR" set "EXE_DIR=%%B"
        if /i "%%A"=="GAME_EXE_PATH" set "GAME_EXE_PATH=%%B"
        if /i "%%A"=="GAME_NAME" set "DEFAULT_GAME_NAME=%%B"
        if /i "%%A"=="DETECTED_APPID" set "DETECTED_APPID=%%B"
        if /i "%%A"=="CANDIDATE_EXES" set "CANDIDATE_EXES=%%B"
    )
)

if "%DEFAULT_GAME_NAME%"=="" (
    for %%I in ("%TARGET_DIR%") do set "DEFAULT_GAME_NAME=%%~nxI"
)

echo [DETECTION] Detected Engine Type: %ENGINE_TYPE%
echo [DETECTION] Executable Location: "%EXE_DIR%"
if not "%GAME_EXE_PATH%"=="" echo [DETECTION] Game Executable: "%GAME_EXE_PATH%"
if not "%DETECTED_APPID%"=="" echo [DETECTION] Detected Steam AppID: %DETECTED_APPID%
echo:

:: Executable Location and Selection Prompt
echo Confirm or Change Executable Location:
echo [1] Use detected executable and location (Default)
echo [2] Select game executable using File Explorer (GUI Picker)
echo [3] Enter game executable path manually (.exe)
echo [4] Enter executable directory path manually (folder)
echo:
set "EXE_CHOICE=1"
if not "%REFIX_NON_INTERACTIVE%"=="1" set /p "EXE_CHOICE=Select option [1-4] (Default: 1): "
if "%EXE_CHOICE%"=="" set "EXE_CHOICE=1"

if "%EXE_CHOICE%"=="2" goto EXE_CHOICE_GUI
if "%EXE_CHOICE%"=="3" goto EXE_CHOICE_MANUAL_FILE
if "%EXE_CHOICE%"=="4" goto EXE_CHOICE_MANUAL_DIR
goto AFTER_EXE_CHOICE

:EXE_CHOICE_GUI
echo:
echo [INFO] Opening File Explorer to select executable...
set "REFIX_EXE_DIR=%EXE_DIR%"
if exist "%BIN_DIR%\select_exe.ps1" (
    set "USER_EXE="
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\select_exe.ps1" -InitialDir "%EXE_DIR%" ^<nul`) do (
        set "USER_EXE=%%I"
    )
    if not "%USER_EXE%"=="" (
        set "GAME_EXE_PATH=%USER_EXE%"
        for %%F in ("%USER_EXE%") do set "EXE_DIR=%%~dpF"
        if "%EXE_DIR:~-1%"=="\" set "EXE_DIR=%EXE_DIR:~0,-1%"
        for %%F in ("%USER_EXE%") do set "DEFAULT_GAME_NAME=%%~nF"
    )
)
goto AFTER_EXE_CHOICE

:EXE_CHOICE_MANUAL_FILE
echo:
set /p "MANUAL_EXE_PATH=Enter full executable file path (.exe): "
if not "%MANUAL_EXE_PATH%"=="" (
    set "MANUAL_EXE_PATH=%MANUAL_EXE_PATH:"=%"
    set "GAME_EXE_PATH=%MANUAL_EXE_PATH%"
    for %%F in ("%MANUAL_EXE_PATH%") do set "EXE_DIR=%%~dpF"
    if "%EXE_DIR:~-1%"=="\" set "EXE_DIR=%EXE_DIR:~0,-1%"
    for %%F in ("%MANUAL_EXE_PATH%") do set "DEFAULT_GAME_NAME=%%~nF"
)
goto AFTER_EXE_CHOICE

:EXE_CHOICE_MANUAL_DIR
echo:
set /p "MANUAL_EXE_DIR=Enter executable directory path: "
if not "%MANUAL_EXE_DIR%"=="" (
    set "MANUAL_EXE_DIR=%MANUAL_EXE_DIR:"=%"
    set "EXE_DIR=%MANUAL_EXE_DIR%"
    if "%EXE_DIR:~-1%"=="\" set "EXE_DIR=%EXE_DIR:~0,-1%"
)
goto AFTER_EXE_CHOICE

:AFTER_EXE_CHOICE
echo:
set "USER_ENGINE_CHOICE="
if not "%REFIX_NON_INTERACTIVE%"=="1" set /p "USER_ENGINE_CHOICE=Confirm Engine [Unity/Godot/Unreal/Native] (Default: %ENGINE_TYPE%): "
if not "%USER_ENGINE_CHOICE%"=="" set "ENGINE_TYPE=%USER_ENGINE_CHOICE%"

:: Read existing settings from ReFix.ini if it already exists in target
set "EXISTING_REAL_APPID=%DETECTED_APPID%"
set "EXISTING_USERNAME="
set "EXISTING_STEAMID="
set "EXISTING_LAN_PORT=47584"
if exist "%EXE_DIR%\ReFix.ini" (
    for /f "usebackq tokens=1,* delims==" %%A in ("%EXE_DIR%\ReFix.ini") do (
        if /i "%%A"=="RealAppId" (
            if not "%%B"=="" if not "%%B"=="0" if not "%%B"=="480" set "EXISTING_REAL_APPID=%%B"
        )
        if /i "%%A"=="Name" set "EXISTING_USERNAME=%%B"
        if /i "%%A"=="SteamId" set "EXISTING_STEAMID=%%B"
        if /i "%%A"=="ListenPort" set "EXISTING_LAN_PORT=%%B"
    )
)
if "%EXISTING_REAL_APPID%"=="" set "EXISTING_REAL_APPID=480"

set "GAME_NAME="
if not "%REFIX_NON_INTERACTIVE%"=="1" set /p "GAME_NAME=Enter Game Name (Default: %DEFAULT_GAME_NAME%): "
if "%GAME_NAME%"=="" set "GAME_NAME=%DEFAULT_GAME_NAME%"

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
set "ONLINE_MODE_CHOICE=1"
if not "%REFIX_NON_INTERACTIVE%"=="1" set /p "ONLINE_MODE_CHOICE=Select Option [1-2] (Default: 1): "
if "%ONLINE_MODE_CHOICE%"=="" set "ONLINE_MODE_CHOICE=1"

set "ONLINE_MODE_NAME=valve"
if "%ONLINE_MODE_CHOICE%"=="2" set "ONLINE_MODE_NAME=goldberg"

if "%ONLINE_MODE_NAME%"=="goldberg" goto GOLDBERG_PROMPTS

:VALVE_PROMPTS
echo:
echo [INFO] Selected Mode: ReFix Online via Steam
echo:
set "MASK_APPID=480"
set "REAL_APPID=%EXISTING_REAL_APPID%"
if "%REAL_APPID%"=="" set "REAL_APPID=480"
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    if not "%EXISTING_REAL_APPID%"=="" if not "%EXISTING_REAL_APPID%"=="480" (
        set /p "REAL_APPID=Enter Real Steam AppID (Default: %EXISTING_REAL_APPID%): "
        if "%REAL_APPID%"=="" set "REAL_APPID=%EXISTING_REAL_APPID%"
    ) else (
        set /p "REAL_APPID=Enter Real Steam AppID: "
        if "%REAL_APPID%"=="" set "REAL_APPID=480"
    )
)

set "CUSTOM_USERNAME=%EXISTING_USERNAME%"
if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%USERNAME%"
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    set /p "CUSTOM_USERNAME=Enter Custom Username (Optional - Enter to use Steam name): "
    if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%EXISTING_USERNAME%"
    if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%USERNAME%"
)
set "LAN_PORT=47584"
set "CUSTOM_BROADCASTS="
goto AFTER_MODE_PROMPTS

:GOLDBERG_PROMPTS
echo:
echo [INFO] Selected Mode: Re:Goldberg LAN without Steam
echo:
set "CUSTOM_USERNAME=%EXISTING_USERNAME%"
if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%USERNAME%"
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    set /p "CUSTOM_USERNAME=Enter Player Username (Leave empty to auto-generate): "
    if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%EXISTING_USERNAME%"
    if "%CUSTOM_USERNAME%"=="" set "CUSTOM_USERNAME=%USERNAME%"
)

set "REAL_APPID=%EXISTING_REAL_APPID%"
if "%REAL_APPID%"=="" set "REAL_APPID=480"
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    if not "%EXISTING_REAL_APPID%"=="" if not "%EXISTING_REAL_APPID%"=="480" (
        set /p "REAL_APPID=Enter Real Steam AppID (Default: %EXISTING_REAL_APPID%): "
        if "%REAL_APPID%"=="" set "REAL_APPID=%EXISTING_REAL_APPID%"
    ) else (
        set /p "REAL_APPID=Enter Real Steam AppID: "
        if "%REAL_APPID%"=="" set "REAL_APPID=480"
    )
)
set "MASK_APPID=%REAL_APPID%"

set "LAN_PORT=%EXISTING_LAN_PORT%"
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    set /p "LAN_PORT=Enter LAN Listen Port (Default: %EXISTING_LAN_PORT%): "
    if "%LAN_PORT%"=="" set "LAN_PORT=%EXISTING_LAN_PORT%"
)

set "CUSTOM_BROADCASTS="
if not "%REFIX_NON_INTERACTIVE%"=="1" (
    set /p "CUSTOM_BROADCASTS=Enter Custom Broadcast IPs [Comma-separated, Optional]: "
)
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
set "DLC_CHOICE=1"
if not "%REFIX_NON_INTERACTIVE%"=="1" set /p "DLC_CHOICE=Select Option [1-3] (Default: 1): "
if "%DLC_CHOICE%"=="" set "DLC_CHOICE=1"

set "DLC_MODE=all"
set "DLCS=all"

if "%DLC_CHOICE%"=="2" (
    set "DLC_MODE=none"
    set "DLCS=none"
)
if "%DLC_CHOICE%"=="3" goto DLC_CUSTOM_SELECT
goto AFTER_DLC_SELECT

:DLC_CUSTOM_SELECT
set "DLC_MODE=custom"
set "TMP_DLC_OUT=%TEMP%\refix_dlc_sel_%RANDOM%.txt"
if exist "%BIN_DIR%\select_dlcs.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\select_dlcs.ps1" -AppId "%REAL_APPID%" -GameName "%GAME_NAME%" -OutputFile "%TMP_DLC_OUT%"
    if exist "%TMP_DLC_OUT%" (
        set "DLC_MODE_SET="
        for /f "usebackq delims=" %%L in ("%TMP_DLC_OUT%") do (
            if not defined DLC_MODE_SET (
                set "DLC_MODE=%%L"
                set "DLC_MODE_SET=1"
            ) else (
                set "DLCS=%%L"
            )
        )
        set "DLC_MODE_SET="
        del "%TMP_DLC_OUT%" >nul 2>&1
    )
) else (
    set /p "DLCS=Enter DLC AppIDs [Comma-separated, e.g. 12345,67890]: "
)

:AFTER_DLC_SELECT
set "BYPASS_LICENSE=true"
if "%DLC_MODE%"=="none" set "BYPASS_LICENSE=false"

if "%ONLINE_MODE_NAME%"=="goldberg" (
    echo:
    echo ====================================================================
    echo             Windows Firewall Configuration for LAN
    echo ====================================================================
    echo:
    echo  Do you want to automatically authorize the game and UDP port %LAN_PORT%
    echo  in Windows Firewall to ensure LAN discovery?
    echo:
    set "FW_CHOICE="
    set /p "FW_CHOICE=Apply Firewall rules? [Y/N] (Default: Y): "
    if "%FW_CHOICE%"=="" set "FW_CHOICE=Y"
    if /i "%FW_CHOICE%"=="N" (
        set "FIREWALL_AUTO_APPLY=false"
    ) else (
        set "FIREWALL_AUTO_APPLY=true"
    )
)

echo:
echo ====================================================================
echo Starting ReFix Deployment... (Mode: %ONLINE_MODE_NAME%)
echo ====================================================================

:: Set process environment variables for 100% path safety transport across CMD/PowerShell boundary
set "REFIX_TARGET_DIR=%TARGET_DIR%"
set "REFIX_BIN_DIR=%BIN_DIR%"
set "REFIX_EXE_DIR=%EXE_DIR%"
set "REFIX_ENGINE_TYPE=%ENGINE_TYPE%"
set "REFIX_ONLINE_MODE=%ONLINE_MODE_NAME%"
set "REFIX_GAME_NAME=%GAME_NAME%"
set "REFIX_USER_NAME=%CUSTOM_USERNAME%"
set "REFIX_REAL_APPID=%REAL_APPID%"
set "REFIX_MASK_APPID=%MASK_APPID%"
set "REFIX_LANGUAGE=english"
set "REFIX_DLCS=%DLCS%"
set "REFIX_DLC_MODE=%DLC_MODE%"
set "REFIX_LISTEN_PORT=%LAN_PORT%"
set "REFIX_CUSTOM_BROADCASTS=%CUSTOM_BROADCASTS%"
set "REFIX_PHOTON_APPID=%PHOTON_APPID%"
set "REFIX_PHOTON_REGION=%PHOTON_REGION%"

if not exist "%BIN_DIR%\deploy_helper.ps1" (
    echo [ERROR] Required script missing: "%BIN_DIR%\deploy_helper.ps1"
    goto ERROR_EXIT
)

echo [1/3] Running ReFix deployment and topology engine...
powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\deploy_helper.ps1" -TargetDir "%TARGET_DIR%" -BinDir "%BIN_DIR%" -ExeDir "%EXE_DIR%" -EngineType "%ENGINE_TYPE%" -OnlineMode "%ONLINE_MODE_NAME%" -GameName "%GAME_NAME%" -UserName "%CUSTOM_USERNAME%" -RealAppId "%REAL_APPID%" -MaskAppId "%MASK_APPID%" -Language "english" -DLCs "%DLCS%" -DLCMode "%DLC_MODE%" -ListenPort "%LAN_PORT%" -CustomBroadcasts "%CUSTOM_BROADCASTS%" -PhotonAppId "%PHOTON_APPID%" -PhotonRegion "%PHOTON_REGION%"
if errorlevel 1 (
    echo:
    echo [ERROR] Deployment engine reported failure! Aborting.
    goto ERROR_EXIT
)

:: Copy Steam Shortcut Installer & PowerShell helper (Valve mode only)
if "%ONLINE_MODE_NAME%"=="valve" (
    echo [2/3] Copying Steam Shortcut scripts...
    if exist "%BIN_DIR%\add_steam_shortcut.ps1" (
        copy /y "%BIN_DIR%\add_steam_shortcut.ps1" "%EXE_DIR%\add_steam_shortcut.ps1" >nul
    )
    if exist "%BIN_DIR%\Install_ReFix_Steam_Shortcut.bat" (
        copy /y "%BIN_DIR%\Install_ReFix_Steam_Shortcut.bat" "%EXE_DIR%\Install_ReFix_Steam_Shortcut.bat" >nul
    )
    echo [OK] Deployed Steam Shortcut scripts to "%EXE_DIR%"
) else (
    echo [2/3] Finalizing portable configuration...
)

:: Apply Windows Firewall Rules dynamically
if "%FIREWALL_AUTO_APPLY%"=="true" (
    echo:
    echo [3/3] Configuring Windows Firewall rules for game and LAN network...
    if exist "%BIN_DIR%\apply_firewall.ps1" (
        powershell -NoProfile -ExecutionPolicy Bypass -File "%BIN_DIR%\apply_firewall.ps1" -GameExe "%GAME_EXE_PATH%" -GameName "%GAME_NAME%" -LanPort "%LAN_PORT%" -Mode "%ONLINE_MODE_NAME%"
    )
) else (
    echo [3/3] Skipping Windows Firewall configuration...
)

:: ============================================================
:: Post-Deployment Sanity Verification (FASE 6)
:: ============================================================
echo:
echo [VERIFICATION] Verifying deployed files on disk...
if not exist "%EXE_DIR%" (
    echo [ERROR] Target executable directory does not exist: "%EXE_DIR%"
    goto ERROR_EXIT
)
if not exist "%EXE_DIR%\ReFix.ini" (
    echo [ERROR] Verification failed: Mandatory ReFix.ini missing in "%EXE_DIR%"
    goto ERROR_EXIT
)
if not exist "%EXE_DIR%\steam_appid.txt" (
    echo [ERROR] Verification failed: Mandatory steam_appid.txt missing in "%EXE_DIR%"
    goto ERROR_EXIT
)
if "%ONLINE_MODE_NAME%"=="valve" (
    if not exist "%EXE_DIR%\winmm.dll" (
        if exist "%EXE_DIR%\steam_api64.dll" (
            echo [ERROR] Verification failed: Mandatory winmm.dll proxy missing in "%EXE_DIR%"
            goto ERROR_EXIT
        )
    )
)
echo [VERIFICATION OK] Critical deployment files verified in "%EXE_DIR%".

echo:
echo ====================================================================
echo  DEPLOY COMPLETE
echo  Game: %GAME_NAME%  ^|  Engine: %ENGINE_TYPE%
if "%ONLINE_MODE_NAME%"=="goldberg" goto SUMMARY_GOLDBERG

:SUMMARY_VALVE
echo  Mode: ReFix Online via Steam [Spacewar 480]
echo  Location: "%EXE_DIR%"
echo  AppID: MaskAppId=%MASK_APPID%  /  RealAppId=%REAL_APPID%
echo  DLCs: Mode=%DLC_MODE% [%DLCS%]
goto AFTER_SUMMARY

:SUMMARY_GOLDBERG
echo  Mode: Re:Goldberg LAN without Steam [Offline / gbe_fork]
echo  Location: "%EXE_DIR%"
echo  AppID: RealAppId=%REAL_APPID%
echo  DLCs: Mode=%DLC_MODE% [%DLCS%]
echo  LAN Port: %LAN_PORT% [UDP Broadcast Discovery]
echo  Storage: Portable in saves\ [USB / Flash Drive Compatible]
echo  Firewall Helper: "%EXE_DIR%\Configure_LAN_Firewall.bat"
goto AFTER_SUMMARY

:AFTER_SUMMARY
echo ====================================================================
echo:

:: Offer one-click launch if game executable is found
if "%GAME_EXE_PATH%"=="" goto SKIP_LAUNCH_OFFER
if "%REFIX_NON_INTERACTIVE%"=="1" goto SKIP_LAUNCH_OFFER

echo  Executable ready: "%GAME_EXE_PATH%"
echo:
set "LAUNCH_NOW=Y"
set /p "LAUNCH_NOW=Launch game now? [Y/N] (Default: Y): "
if /i "%LAUNCH_NOW%"=="N" goto SKIP_LAUNCH_OFFER

echo:
echo [LAUNCH] Starting "%GAME_NAME%"...
start "" "%GAME_EXE_PATH%"
echo [OK] Game started.
goto SUCCESS_EXIT

:SKIP_LAUNCH_OFFER

if "%ONLINE_MODE_NAME%"=="valve" (
    if "%REFIX_NON_INTERACTIVE%"=="1" goto SUCCESS_EXIT
    set "RUN_INSTALLER=Y"
    set /p "RUN_INSTALLER=Add to Steam as Non-Steam Game? [Y/N] (Default: Y): "
    if /i "%RUN_INSTALLER%"=="N" goto SUCCESS_EXIT

    echo:
    echo [INFO] Running Steam Shortcut Auto-Installer...
    pushd "%EXE_DIR%"
    if exist "Install_ReFix_Steam_Shortcut.bat" (
        call Install_ReFix_Steam_Shortcut.bat "%GAME_NAME%" "%GAME_EXE_PATH%"
    )
    popd
)

:SUCCESS_EXIT
echo:
echo Deployment completed successfully.
echo:
if "%REFIX_NON_INTERACTIVE%"=="" (
    echo Press any key to exit...
    pause >nul
)
exit /b 0

:ERROR_EXIT
echo:
echo [FATAL ERROR] The operation could not be completed. Check the messages above.
echo:
if "%REFIX_NON_INTERACTIVE%"=="" (
    echo Press any key to exit...
    pause >nul
)
exit /b 1
