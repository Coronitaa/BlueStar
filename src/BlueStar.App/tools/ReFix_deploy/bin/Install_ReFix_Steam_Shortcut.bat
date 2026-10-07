@echo off
chcp 65001 >nul
setlocal disabledelayedexpansion
title ReFix - Steam Non-Steam Game Installer

echo ====================================================
echo  ReFix - Steam Shortcut Setup ^& Auto-Installer
echo ====================================================
echo.

set "GAME_NAME=%~1"
if "%GAME_NAME%"=="" (
    if exist "ReFix.ini" (
        for /f "tokens=1,* delims==" %%A in ('type ReFix.ini ^| findstr /i "^GameName="') do (
            set "GAME_NAME=%%B"
        )
    )
)

if "%GAME_NAME%"=="" (
    for %%I in ("%CD%") do set "GAME_NAME=%%~nxI"
)

REM Step 1: Check if Steam is running and close it to update shortcuts safely
tasklist /FI "IMAGENAME eq steam.exe" 2>NUL | find /I /N "steam.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo [+] Steam is currently running. Closing Steam to update library...
    taskkill /F /IM steam.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
) else (
    echo [+] Steam is not running.
)

REM Step 2: Update shortcuts.vdf using native PowerShell script
echo.
echo [+] Injecting Non-Steam Game shortcut into all Steam user profiles...
set "OPT_EXE="
if not "%~2"=="" set "OPT_EXE=-ExePath "%~2""
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0add_steam_shortcut.ps1" -GameName "%GAME_NAME%" %OPT_EXE%

REM Step 3: Relaunch Steam dynamically from Registry or common paths
echo.
echo [+] Restarting Steam interface...
set "STEAM_EXE="
set "_SPATH="

:: 1. Try HKCU Registry SteamExe
for /f "tokens=2*" %%A in ('reg query "HKCU\Software\Valve\Steam" /v "SteamExe" 2^>nul') do (
    set "STEAM_EXE=%%B"
)
if defined STEAM_EXE set "STEAM_EXE=%STEAM_EXE:/=\%"
if defined STEAM_EXE if not exist "%STEAM_EXE%" set "STEAM_EXE="

:: 2. Try HKCU Registry SteamPath + \steam.exe
if "%STEAM_EXE%"=="" (
    for /f "tokens=2*" %%A in ('reg query "HKCU\Software\Valve\Steam" /v "SteamPath" 2^>nul') do (
        set "_SPATH=%%B"
    )
)
if "%STEAM_EXE%"=="" if defined _SPATH (
    set "_SPATH=%_SPATH:/=\%"
)
if "%STEAM_EXE%"=="" if defined _SPATH (
    if exist "%_SPATH%\steam.exe" set "STEAM_EXE=%_SPATH%\steam.exe"
)

:: 3. Try HKLM 64-bit/32-bit Registry InstallPath
if "%STEAM_EXE%"=="" (
    set "_SPATH="
    for /f "tokens=2*" %%A in ('reg query "HKLM\SOFTWARE\WOW6432Node\Valve\Steam" /v "InstallPath" 2^>nul') do (
        set "_SPATH=%%B"
    )
)
if "%STEAM_EXE%"=="" if defined _SPATH (
    set "_SPATH=%_SPATH:/=\%"
)
if "%STEAM_EXE%"=="" if defined _SPATH (
    if exist "%_SPATH%\steam.exe" set "STEAM_EXE=%_SPATH%\steam.exe"
)

:: 4. Try common installation drives
if "%STEAM_EXE%"=="" if exist "%ProgramFiles(x86)%\Steam\steam.exe" set "STEAM_EXE=%ProgramFiles(x86)%\Steam\steam.exe"
if "%STEAM_EXE%"=="" if exist "%ProgramFiles%\Steam\steam.exe" set "STEAM_EXE=%ProgramFiles%\Steam\steam.exe"
if "%STEAM_EXE%"=="" if exist "C:\Steam\steam.exe" set "STEAM_EXE=C:\Steam\steam.exe"
if "%STEAM_EXE%"=="" if exist "D:\Steam\steam.exe" set "STEAM_EXE=D:\Steam\steam.exe"
if "%STEAM_EXE%"=="" if exist "E:\Steam\steam.exe" set "STEAM_EXE=E:\Steam\steam.exe"
if "%STEAM_EXE%"=="" if exist "F:\Steam\steam.exe" set "STEAM_EXE=F:\Steam\steam.exe"

if not "%STEAM_EXE%"=="" (
    echo [+] Launching Steam from: "%STEAM_EXE%"
    start "" "%STEAM_EXE%"
) else (
    echo [WARNING] Could not locate steam.exe automatically. Please launch Steam manually.
)

echo.
echo ====================================================
echo  [SUCCESS] Shortcut '%GAME_NAME%' installed!
echo  Steam has been reloaded. You will find the game in
echo  your Steam library and can launch it directly from Steam
echo  with full Steam Overlay (Shift+Tab) and Online Lobbies.
echo ====================================================
echo.
if "%REFIX_NON_INTERACTIVE%"=="" pause
