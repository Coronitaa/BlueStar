@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

title BlueStar Release Builder (Portable + Setup + MSIX)

REM Verificar dotnet SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo ========================================================================
    echo   [ERROR] No se encontró el comando 'dotnet'.
    echo   Por favor asegúrate de tener instalado .NET 8 SDK y que esté en tu PATH.
    echo   Descarga oficial: https://dotnet.microsoft.com/download/dotnet/8.0
    echo ========================================================================
    echo.
    pause
    exit /b 1
)

REM Si se proporcionan argumentos por consola, pasar directamente; si no, ejecutar modo interactivo
if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -Interactive
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -Version "%~1" -ApiKey "%~2"
)

set "BUILD_EXIT=%ERRORLEVEL%"

if not "%BUILD_EXIT%"=="0" (
    echo.
    echo ========================================================================
    echo   [ERROR] La compilación finalizó con errores. Revisa la salida arriba.
    echo ========================================================================
    echo.
)

pause
exit /b %BUILD_EXIT%
