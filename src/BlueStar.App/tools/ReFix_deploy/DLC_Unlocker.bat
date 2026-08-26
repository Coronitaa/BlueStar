@echo off
setlocal enabledelayedexpansion
title ReFix - Universal DLC Unlocker v1.2 (BLUESTAR Engine)

set "SCRIPT_DIR=%~dp0"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"
set "BIN_DIR=!SCRIPT_DIR!\bin"

echo ====================================================================
echo             ReFix - Universal DLC Unlocker (BLUESTAR)
echo ====================================================================
echo:

if not "%~1"=="" (
    set "TARGET_DIR=%~1"
    goto GOT_TARGET_DIR
)

echo Select deployment target mode:
echo [1] Apply to game in CURRENT folder (!SCRIPT_DIR!)
echo [2] Select game folder using File Explorer (GUI Picker)
echo [3] Enter game folder path manually
echo:
set "TARGET_CHOICE="
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
set "GAME_NAME="
set /p "GAME_NAME=Enter Game Name (Default: !DEFAULT_GAME_NAME!): "
if "!GAME_NAME!"=="" set "GAME_NAME=!DEFAULT_GAME_NAME!"

echo:
echo ====================================================================
echo                     DLC Unlocker Options
echo ====================================================================
echo:
echo  [1] Unlock ALL DLCs (Universal auto-unlock mode)
echo  [2] Choose specific DLCs to unlock (Steam Store catalog or manual IDs)
echo  [3] Unlock NO DLCs (SmokeAPI / CreamAPI with all DLCs locked)
echo  [4] Restore original Steam DLLs (Uninstall DLC Unlocker)
echo  [5] Check DLC Unlocker installation status
echo:
set "ACTION_CHOICE="
set /p "ACTION_CHOICE=Select Option [1-5] (Default: 1): "
if "!ACTION_CHOICE!"=="" set "ACTION_CHOICE=1"

if "!ACTION_CHOICE!"=="4" goto DO_UNINSTALL
if "!ACTION_CHOICE!"=="5" goto DO_STATUS

:DO_INSTALL
echo:
set "APPID="
if not "!DETECTED_APPID!"=="" if not "!DETECTED_APPID!"=="480" (
    set /p "APPID=Enter Real Steam AppID (Default: !DETECTED_APPID!): "
    if "!APPID!"=="" set "APPID=!DETECTED_APPID!"
) else (
    set /p "APPID=Enter Real Steam AppID: "
)

set "DLC_MODE=all"
if "!ACTION_CHOICE!"=="2" set "DLC_MODE=custom"
if "!ACTION_CHOICE!"=="3" set "DLC_MODE=none"

echo:
echo [INFO] Running DLC Unlocker (Mode: !DLC_MODE!)...
if exist "!BIN_DIR!\dlc_unlocker.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\dlc_unlocker.ps1" -TargetDir "!TARGET_DIR!" -BinDir "!BIN_DIR!" -Action "install" -AppId "!APPID!" -GameName "!GAME_NAME!" -DLCMode "!DLC_MODE!"
) else (
    echo [ERROR] Could not find !BIN_DIR!\dlc_unlocker.ps1
    goto ERROR_EXIT
)
goto AFTER_ACTION

:DO_UNINSTALL
echo:
echo [INFO] Restoring original files...
if exist "!BIN_DIR!\dlc_unlocker.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\dlc_unlocker.ps1" -TargetDir "!TARGET_DIR!" -BinDir "!BIN_DIR!" -Action "uninstall"
)
goto AFTER_ACTION

:DO_STATUS
echo:
if exist "!BIN_DIR!\dlc_unlocker.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!BIN_DIR!\dlc_unlocker.ps1" -TargetDir "!TARGET_DIR!" -BinDir "!BIN_DIR!" -Action "status"
)
goto AFTER_ACTION

:AFTER_ACTION
echo:
if not "!GAME_EXE_PATH!"=="" (
    set "LAUNCH_NOW="
    set /p "LAUNCH_NOW=Launch game now? [Y/N] (Default: N): "
    if /i "!LAUNCH_NOW!"=="Y" (
        echo [LAUNCH] Starting "!GAME_NAME!"...
        start "" "!GAME_EXE_PATH!"
    )
)

:SUCCESS_EXIT
echo:
echo Operation completed.
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
