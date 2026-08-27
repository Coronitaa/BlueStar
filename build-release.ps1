param(
    [string]$Version = "1.1.0"
)

$ErrorActionPreference = "Stop"

Write-Host "=========================================="
Write-Host "  BlueStar Release Builder                "
Write-Host "=========================================="

$rootDir = $PSScriptRoot
$distDir = Join-Path $rootDir "dist"
$publishDir = Join-Path $distDir "publish"
$portableZip = Join-Path $distDir ('BlueStar-v' + $Version + '-Portable-win-x64.zip')
$setupExe = Join-Path $distDir ('BlueStar-v' + $Version + '-Setup-win-x64.exe')

# 1. Clean previous dist output
if (Test-Path $distDir) {
    Write-Host "Cleaning previous dist output..."
    Get-ChildItem -Path $distDir | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# 2. Publish Self-Contained Win-x64
Write-Host "Publishing self-contained win-x64 release build..."
$projectPath = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 3. Create Portable Zip
Write-Host "Packaging Portable Zip archive..."
if (Test-Path $portableZip) {
    Remove-Item $portableZip -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipItem = Get-Item $portableZip
$zipMb = [math]::Round($zipItem.Length / 1048576, 2)
Write-Host ('Portable Zip created: ' + $portableZip + ' (' + $zipMb + ' MB)')

# 4. Compile Inno Setup Installer
Write-Host "Compiling Windows Installer with Inno Setup..."
$isccPaths = @(
    "C:\Users\Valen\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$isccExe = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $isccExe) {
    Write-Warning "ISCC.exe not found! Please check Inno Setup installation."
} else {
    $issFile = Join-Path $rootDir "installer.iss"
    & $isccExe "/Qp" ('/DMyAppVersion=' + $Version) $issFile

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed!"
        exit 1
    }

    if (Test-Path $setupExe) {
        $setupItem = Get-Item $setupExe
        $setupMb = [math]::Round($setupItem.Length / 1048576, 2)
        Write-Host ('Setup Installer created: ' + $setupExe + ' (' + $setupMb + ' MB)')
    }
}

Write-Host "=========================================="
Write-Host "  Release artifacts generated successfully! "
Write-Host "=========================================="
Get-ChildItem -Path $distDir -File | Select-Object Name, Length, LastWriteTime
