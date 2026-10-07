@echo off
chcp 65001 >nul
setlocal disabledelayedexpansion
title ReFix - Universal Uninstall ^& Restore Tool

echo ====================================================================
echo                   ReFix Uninstall ^& Restore Tool
echo ====================================================================
echo:

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "BIN_DIR=%SCRIPT_DIR%\bin"

if not "%~1"=="" (
    set "TARGET_DIR=%~1"
    goto GOT_TARGET_DIR
)

echo Select target game folder to restore:
echo [1] Restore game in CURRENT folder (%SCRIPT_DIR%)
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
    goto END
)

:: Strip trailing backslash and quotes if present
set "TARGET_DIR=%TARGET_DIR:"=%"
:TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"==" " set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"=="\" set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR
if "%TARGET_DIR:~-1%"=="/" set "TARGET_DIR=%TARGET_DIR:~0,-1%" & goto TRIM_TARGET_DIR

if not exist "%TARGET_DIR%" (
    echo [ERROR] Directory does not exist: "%TARGET_DIR%"
    goto END
)

echo:
echo ====================================================================
echo Restoring Original Game Files in: "%TARGET_DIR%"
echo ====================================================================
echo:

:: 1. Restore winmm_o.dll to winmm.dll if backup exists
for /f "delims=" %%W in ('dir /s /b "%TARGET_DIR%\winmm_o.dll" 2^>nul') do (
    if exist "%%~dpWwinmm.dll" del /f /q "%%~dpWwinmm.dll"
    ren "%%W" "winmm.dll"
    echo [OK] Restored original winmm.dll in "%%~dpW"
)

:: 2. Restore original steam_api64.dll and steam_api.dll from backup
for /f "delims=" %%V in ('dir /s /b "%TARGET_DIR%\steam_api64_valve.dll" "%TARGET_DIR%\steam_api64_o.dll" 2^>nul') do (
    if exist "%%~dpVsteam_api64.dll" del /f /q "%%~dpVsteam_api64.dll"
    ren "%%V" "steam_api64.dll"
    echo [OK] Restored original steam_api64.dll in "%%~dpV"
)
for /f "delims=" %%V in ('dir /s /b "%TARGET_DIR%\steam_api_valve.dll" "%TARGET_DIR%\steam_api_o.dll" 2^>nul') do (
    if exist "%%~dpVsteam_api.dll" del /f /q "%%~dpVsteam_api.dll"
    ren "%%V" "steam_api.dll"
    echo [OK] Restored original steam_api.dll in "%%~dpV"
)

:: 3. Restore EOSSDK_original.dll to EOSSDK-Win64-Shipping.dll
for /f "delims=" %%E in ('dir /s /b "%TARGET_DIR%\EOSSDK_original.dll" 2^>nul') do (
    if exist "%%~dpEEOSSDK-Win64-Shipping.dll" del /f /q "%%~dpEEOSSDK-Win64-Shipping.dll"
    ren "%%E" "EOSSDK-Win64-Shipping.dll"
    echo [OK] Restored original EOSSDK-Win64-Shipping.dll in "%%~dpE"
)

:: 4. Remove RedboneEOS.dll proxy files
for /f "delims=" %%R in ('dir /s /b "%TARGET_DIR%\RedboneEOS.dll" 2^>nul') do (
    del /f /q "%%R"
    echo [OK] Removed RedboneEOS.dll: %%R
)

:: 5. Restore original Assembly-CSharp.dll.orig and com.rlabrecque.steamworks.net.dll.orig for Unity games
for /f "delims=" %%A in ('dir /s /b "%TARGET_DIR%\Assembly-CSharp.dll.orig" 2^>nul') do (
    if exist "%%~dpAAssembly-CSharp.dll" del /f /q "%%~dpAAssembly-CSharp.dll"
    ren "%%A" "Assembly-CSharp.dll"
    echo [OK] Restored original Assembly-CSharp.dll in "%%~dpA"
)
for /f "delims=" %%A in ('dir /s /b "%TARGET_DIR%\com.rlabrecque.steamworks.net.dll.orig" 2^>nul') do (
    if exist "%%~dpAcom.rlabrecque.steamworks.net.dll" del /f /q "%%~dpAcom.rlabrecque.steamworks.net.dll"
    ren "%%A" "com.rlabrecque.steamworks.net.dll"
    echo [OK] Restored original com.rlabrecque.steamworks.net.dll in "%%~dpA"
)

:: 5b. Restore original SteamStub protected executables if unpacked by ReFix
for /f "delims=" %%S in ('dir /s /b "%TARGET_DIR%\*.steamstub.exe" "%TARGET_DIR%\*.steamstub" 2^>nul') do (
    if exist "%%~dpS%%~nS.exe" del /f /q "%%~dpS%%~nS.exe"
    ren "%%S" "%%~nS.exe"
    echo [OK] Restored original SteamStub executable in "%%~dpS"
)

:: 6. Remove ReFix proxies, DLC unlocker files, and artifacts
for /f "delims=" %%F in ('dir /s /b "%TARGET_DIR%\winmm.dll" "%TARGET_DIR%\winhttp.dll" "%TARGET_DIR%\doorstop_config.ini" "%TARGET_DIR%\Kirigiri.ini" "%TARGET_DIR%\ReFix.ini" "%TARGET_DIR%\ReFix.log" "%TARGET_DIR%\steam_appid.txt" "%TARGET_DIR%\local_save.txt" "%TARGET_DIR%\steam_interfaces.txt" "%TARGET_DIR%\add_steam_shortcut.ps1" "%TARGET_DIR%\add_steam_shortcut.py" "%TARGET_DIR%\Install_ReFix_Steam_Shortcut.bat" "%TARGET_DIR%\Configure_LAN_Firewall.bat" "%TARGET_DIR%\Configurar_Firewall_LAN.bat" "%TARGET_DIR%\cream_api.ini" "%TARGET_DIR%\SmokeAPI.config.json" "%TARGET_DIR%\SmokeAPI.json" "%TARGET_DIR%\SmokeAPI.log" "%TARGET_DIR%\SmokeAPI.cache.json" 2^>nul') do (
    del /f /q "%%F"
    echo [OK] Removed ReFix file: %%F
)

:: 7. Remove ReFix directories recursively (steam_settings, saves, BepInEx)
for /d /r "%TARGET_DIR%" %%D in (steam_settings saves BepInEx) do (
    if exist "%%D" (
        rmdir /s /q "%%D"
        echo [OK] Removed directory: %%D
    )
)

echo:
echo ====================================================================
echo SUCCESS: Game folder successfully restored to original state!
echo ====================================================================

:END
echo:
if "%REFIX_NON_INTERACTIVE%"=="" (
    echo Press any key to exit...
    pause >nul
)
exit /b 0
