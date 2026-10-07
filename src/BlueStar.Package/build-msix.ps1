<#
.SYNOPSIS
    Builds BlueStar as an MSIX package for Microsoft Store submission.

.DESCRIPTION
    This script builds the BlueStar WPF application and packages it as an MSIX
    suitable for uploading to the Microsoft Store via Partner Center.

    When submitted to the Microsoft Store, Microsoft signs the package with their
    trusted certificate, which completely eliminates SmartScreen warnings.

.PARAMETER Version
    The version number for the package. Default: 1.4.3

.PARAMETER Configuration
    Build configuration. Default: Release

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Version "1.5.0"
#>

param(
    [string]$Version = "1.4.3",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  BlueStar MSIX Package Builder (v$Version)" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

$rootDir    = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$packageDir = $PSScriptRoot
$distDir    = Join-Path $rootDir "dist"
$appProject = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"

# Parse version for MSIX (must be x.y.z.w format)
$versionParts = $Version.Split('.')
while ($versionParts.Count -lt 4) { $versionParts += "0" }
$msixVersion = $versionParts -join '.'

# 1. Generate tile images if they don't exist
$imagesDir = Join-Path $packageDir "Images"
if (-not (Test-Path (Join-Path $imagesDir "StoreLogo.png"))) {
    Write-Host "[1/4] Generating MSIX tile images..." -ForegroundColor Yellow
    & (Join-Path $packageDir "generate-tiles.ps1")
} else {
    Write-Host "[1/4] Tile images already exist, skipping generation." -ForegroundColor Gray
}

# 2. Update Package.appxmanifest version
Write-Host "[2/4] Updating manifest version to $msixVersion..." -ForegroundColor Yellow
$manifestPath = Join-Path $packageDir "Package.appxmanifest"
$manifestContent = Get-Content $manifestPath -Raw
$manifestContent = $manifestContent -replace '(?<=<Identity[\s\S]*?Version=)"[\d.]+"', """$msixVersion"""
Set-Content -Path $manifestPath -Value $manifestContent -Encoding UTF8

# 3. Build the MSIX package
Write-Host "[3/4] Building MSIX package..." -ForegroundColor Yellow

$msixOutputDir = Join-Path $distDir "msix"
if (Test-Path $msixOutputDir) {
    Remove-Item $msixOutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $msixOutputDir -Force | Out-Null

# Build using dotnet publish for the app first
Write-Host "  Publishing self-contained app for x64..." -ForegroundColor Gray
& dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# Try to build the MSIX using MSBuild (requires VS or Build Tools)
$msbuildPaths = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)

$msbuild = $msbuildPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($msbuild) {
    Write-Host "  Building MSIX with MSBuild..." -ForegroundColor Gray
    $wapproj = Join-Path $packageDir "BlueStar.Package.wapproj"

    & $msbuild $wapproj `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:UapAppxPackageBuildMode=StoreUpload `
        /p:AppxPackageDir="$msixOutputDir\" `
        /p:AppxBundle=Always `
        /p:AppxBundlePlatforms=x64 `
        /p:AppxPackageSigningEnabled=false `
        /restore `
        /v:minimal

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "MSBuild MSIX packaging failed. Falling back to makeappx..."
    } else {
        Write-Host "  MSIX package built successfully!" -ForegroundColor Green
    }
}

# Fallback: Use MakeAppx.exe directly if MSBuild approach failed
$msixFile = Get-ChildItem -Path $msixOutputDir -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$msixBundle = Get-ChildItem -Path $msixOutputDir -Filter "*.msixbundle" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $msixFile -and -not $msixBundle) {
    Write-Host "  Using MakeAppx.exe fallback..." -ForegroundColor Yellow

    # Find MakeAppx.exe
    $sdkPaths = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter "10.*" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending

    $makeappx = $null
    foreach ($sdkPath in $sdkPaths) {
        $candidate = Join-Path $sdkPath.FullName "x64\makeappx.exe"
        if (Test-Path $candidate) {
            $makeappx = $candidate
            break
        }
    }

    if (-not $makeappx) {
        $appCert = "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\makeappx.exe"
        if (Test-Path $appCert) { $makeappx = $appCert }
    }

    if ($makeappx) {
        $publishDir = Join-Path $rootDir "src\BlueStar.App\bin\Release\net8.0-windows\win-x64\publish"
        if (-not (Test-Path $publishDir)) {
            $publishDir = Join-Path $rootDir "dist\publish"
        }

        $mappingFile = Join-Path $msixOutputDir "mapping.txt"
        $mappingContent = @("[Files]")
        $mappingContent += "`"$manifestPath`" `"AppxManifest.xml`""

        # Add images
        Get-ChildItem -Path $imagesDir -Filter "*.png" | ForEach-Object {
            $mappingContent += "`"$($_.FullName)`" `"Images\$($_.Name)`""
        }

        # Add app files
        if (Test-Path $publishDir) {
            Get-ChildItem -Path $publishDir -Recurse -File | ForEach-Object {
                $relativePath = $_.FullName.Substring($publishDir.Length + 1)
                $mappingContent += "`"$($_.FullName)`" `"BlueStar.App\$relativePath`""
            }
        }

        $mappingContent | Set-Content -Path $mappingFile -Encoding UTF8

        $outputMsix = Join-Path $msixOutputDir "BlueStar-v${Version}-win-x64.msix"
        & $makeappx pack /f $mappingFile /p $outputMsix /o

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  MSIX created: $outputMsix" -ForegroundColor Green
        } else {
            Write-Warning "MakeAppx.exe failed. You may need to install the Windows SDK."
        }
    } else {
        Write-Warning "MakeAppx.exe not found. Install Windows 10 SDK from Visual Studio Installer."
        Write-Host "  You can also create the MSIX through Visual Studio: Right-click BlueStar.Package > Publish > Create App Packages" -ForegroundColor Yellow
    }
}

# 4. Summary
Write-Host "`n================================================" -ForegroundColor Cyan
Write-Host "  MSIX Build Summary" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

$packages = Get-ChildItem -Path $msixOutputDir -Include *.msix,*.msixbundle,*.msixupload -Recurse -ErrorAction SilentlyContinue
if ($packages) {
    foreach ($pkg in $packages) {
        $sizeMb = [math]::Round($pkg.Length / 1MB, 2)
        Write-Host "  Package: $($pkg.Name) ($sizeMb MB)" -ForegroundColor Green
    }
    Write-Host "`n  Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Create a developer account at https://storedeveloper.microsoft.com" -ForegroundColor White
    Write-Host "  2. Go to Partner Center > Apps & Games > New Product" -ForegroundColor White
    Write-Host "  3. Upload the .msixupload or .msixbundle file" -ForegroundColor White
    Write-Host "  4. Microsoft will sign it with their trusted certificate!" -ForegroundColor White
} else {
    Write-Host "  No MSIX packages were generated." -ForegroundColor Red
    Write-Host "  Use Visual Studio to create the package manually:" -ForegroundColor Yellow
    Write-Host "  1. Open BlueStar.sln in Visual Studio" -ForegroundColor White
    Write-Host "  2. Right-click BlueStar.Package > Publish > Create App Packages" -ForegroundColor White
    Write-Host "  3. Select 'Microsoft Store' as distribution method" -ForegroundColor White
}
