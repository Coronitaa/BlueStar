<#
.SYNOPSIS
    Builds BlueStar as an MSIX package for Microsoft Store submission or local testing.

.DESCRIPTION
    This script builds the BlueStar WPF application and packages it as an MSIX.
    Supports two distinct packaging modes:
    1. 'Store' (Default): Generates an unsigned .msixupload bundle with symbols via MSBuild
       and the Windows Application Packaging Project (WAPProj). This is the only valid,
       compliant format for Microsoft Store ingestion via Partner Center.
    2. 'LocalTest': Generates a locally signed .msix for developer side-loading and testing.
       Local test packages are NEVER suitable for Microsoft Store submission.

.PARAMETER Target
    Packaging target: 'Store' (default), 'LocalTest', or 'All'.

.PARAMETER Version
    The 4-part version number for the package (e.g. 1.4.5.0). Defaults to 1.4.5.0.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    .\build-msix.ps1 -Target Store
    .\build-msix.ps1 -Target LocalTest
#>

param(
    [ValidateSet("Store", "LocalTest", "All")]
    [string]$Target = "Store",
    [string]$Version = "1.4.5.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  BlueStar MSIX Package Builder (v$Version)" -ForegroundColor Cyan
Write-Host "  Target Mode: $Target | Config: $Configuration" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

$rootDir    = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$packageDir = $PSScriptRoot
$distDir    = Join-Path $rootDir "dist"
$appProject = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"

# Ensure 4-part version format
$versionParts = $Version.Split('.')
while ($versionParts.Count -lt 4) { $versionParts += "0" }
$msixVersion = $versionParts[0..3] -join '.'

# 1. Generate tile images if missing
$imagesDir = Join-Path $packageDir "Images"
if (-not (Test-Path (Join-Path $imagesDir "Square44x44Logo.targetsize-24_altform-unplated.png"))) {
    Write-Host "[1/4] Generating MSIX tile images (including unplated transparent assets)..." -ForegroundColor Yellow
    & (Join-Path $packageDir "generate-tiles.ps1")
} else {
    Write-Host "[1/4] Tile assets present." -ForegroundColor Gray
}

# 2. Sync Package.appxmanifest version if needed (non-destructive)
$manifestPath = Join-Path $packageDir "Package.appxmanifest"
$manifestContent = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
$currentVersionMatch = [regex]::Match($manifestContent, '(?<=<Identity\b[^>]*?\bVersion=")[^"]+')
if ($currentVersionMatch.Success -and $currentVersionMatch.Value -ne $msixVersion) {
    Write-Host "[2/4] Synchronizing manifest version to $msixVersion..." -ForegroundColor Yellow
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    $updatedManifest = [regex]::Replace($manifestContent, '(?<=<Identity\b[^>]*?\bVersion=")[^"]+', $msixVersion)
    [System.IO.File]::WriteAllText($manifestPath, $updatedManifest, $utf8NoBom)
} else {
    Write-Host "[2/4] Manifest version already in sync ($msixVersion)." -ForegroundColor Gray
}

# 3. Publish app self-contained win-x64
$msixOutputDir = Join-Path $distDir "msix"
if (-not (Test-Path $msixOutputDir)) {
    New-Item -ItemType Directory -Path $msixOutputDir -Force | Out-Null
}

$appPublishDir = Join-Path $rootDir "dist\publish"
Write-Host "[3/4] Publishing application binaries (self-contained win-x64)..." -ForegroundColor Yellow
& dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $appPublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 4. Packaging execution
Write-Host "[4/4] Generating packages for target: $Target..." -ForegroundColor Yellow

# Locate MSBuild and DesktopBridge tools
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsPath) {
        $candidateMsbuild = Join-Path $vsPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
        if (-not (Test-Path $candidateMsbuild)) {
            $candidateMsbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        }
        if (Test-Path $candidateMsbuild) {
            $msbuild = $candidateMsbuild
        }
    }
}

$desktopBridgeProps = $null
if ($msbuild) {
    $vsRoot = Split-Path (Split-Path (Split-Path (Split-Path $msbuild -Parent) -Parent) -Parent)
    $candidateProps = Join-Path $vsRoot "MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.props"
    if (Test-Path $candidateProps) {
        $desktopBridgeProps = $candidateProps
    }
}

# --- Target: Store Upload ---
if ($Target -in @("Store", "All")) {
    Write-Host "  [Store Mode] Generating Microsoft Store upload artifact (.msixupload)..." -ForegroundColor Cyan

    if (-not $msbuild -or -not $desktopBridgeProps) {
        Write-Error @"
BUILD REQUIREMENT ERROR:
Microsoft Store packaging requires the Windows Application Packaging Project workload (DesktopBridge).
The target 'Microsoft.DesktopBridge.props' was not found in the installed Visual Studio / Build Tools instance.
To build a valid, compliant Store upload package (.msixupload), install the 'Desktop development with C++' or 'Universal Windows Platform development' workload in Visual Studio Installer.
Do NOT use MakeAppx to fake a Store package, as Partner Center ingestion will reject manually packed Win32 bundles without proper Store symbol maps and package metadata.
"@
        if ($Target -eq "Store") {
            exit 1
        }
    } else {
        $wapproj = Join-Path $packageDir "BlueStar.Package.wapproj"
        $storeOutputDir = Join-Path $msixOutputDir "StoreUpload"
        if (Test-Path $storeOutputDir) { Remove-Item $storeOutputDir -Recurse -Force }
        New-Item -ItemType Directory -Path $storeOutputDir -Force | Out-Null

        & $msbuild $wapproj `
            /p:Configuration=$Configuration `
            /p:Platform=x64 `
            /p:UapAppxPackageBuildMode=StoreUpload `
            /p:AppxPackageDir="$storeOutputDir\" `
            /p:AppxBundle=Always `
            /p:AppxBundlePlatforms=x64 `
            /p:AppxPackageSigningEnabled=false `
            /restore `
            /v:minimal

        if ($LASTEXITCODE -ne 0) {
            Write-Error "MSBuild Store packaging failed!"
            exit 1
        }

        Write-Host "  Microsoft Store upload bundle (.msixupload) built successfully." -ForegroundColor Green
    }
}

# --- Target: Local Testing ---
if ($Target -in @("LocalTest", "All")) {
    Write-Host "  [LocalTest Mode] Generating signed local test package for sideloading..." -ForegroundColor Cyan

    $sdkPaths = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter "10.*" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending

    $makeappx = $null
    $signtool = $null
    foreach ($sdkPath in $sdkPaths) {
        $candidate = Join-Path $sdkPath.FullName "x64\makeappx.exe"
        if (Test-Path $candidate) { $makeappx = $candidate; break }
    }
    foreach ($sdkPath in $sdkPaths) {
        $candidate = Join-Path $sdkPath.FullName "x64\signtool.exe"
        if (Test-Path $candidate) { $signtool = $candidate; break }
    }

    if (-not $makeappx -or -not $signtool) {
        Write-Warning "Windows SDK tools (makeappx / signtool) not found. Skipping local test package generation."
    } else {
        $localOutputDir = Join-Path $msixOutputDir "LocalTest"
        if (Test-Path $localOutputDir) { Remove-Item $localOutputDir -Recurse -Force }
        New-Item -ItemType Directory -Path $localOutputDir -Force | Out-Null

        $mappingFile = Join-Path $localOutputDir "mapping.txt"
        $mappingContent = @("[Files]")
        $mappingContent += "`"$manifestPath`" `"AppxManifest.xml`""

        Get-ChildItem -Path $imagesDir -Filter "*.png" | ForEach-Object {
            $mappingContent += "`"$($_.FullName)`" `"Images\$($_.Name)`""
        }

        Get-ChildItem -Path $appPublishDir -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($appPublishDir.Length + 1)
            $mappingContent += "`"$($_.FullName)`" `"BlueStar.App\$relativePath`""
        }

        $mappingContent | Set-Content -Path $mappingFile -Encoding UTF8

        $localMsix = Join-Path $localOutputDir "BlueStar-v${Version}-win-x64-LocalTest.msix"
        & $makeappx pack /f $mappingFile /p $localMsix /o

        if ($LASTEXITCODE -eq 0) {
            # Extract Publisher CN from manifest to match certificate
            $publisherMatch = [regex]::Match($manifestContent, '(?<=<Identity\b[^>]*?\bPublisher=")[^"]+')
            $subject = if ($publisherMatch.Success) { $publisherMatch.Value } else { "CN=BlueStar Local Test" }

            $cert = Get-ChildItem -Path "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
            if (-not $cert) {
                Write-Host "  Creating dedicated local test certificate for $subject..." -ForegroundColor Gray
                $cert = New-SelfSignedCertificate `
                    -Type Custom `
                    -Subject $subject `
                    -KeyUsage DigitalSignature `
                    -FriendlyName "BlueStar Local Test Certificate" `
                    -CertStoreLocation "Cert:\CurrentUser\My" `
                    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
                    -NotAfter (Get-Date).AddYears(5)
            }

            # Export certificate for user to trust locally
            $cerPath = Join-Path $localOutputDir "BlueStarTestCert.cer"
            Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

            Write-Host "  Signing local test package with: $subject..." -ForegroundColor Gray
            & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $localMsix
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Local test MSIX created and signed: $localMsix" -ForegroundColor Green
                Write-Host "  NOTE: This package is signed with a private local certificate and is ONLY for local sideloading." -ForegroundColor Yellow
            }
        }
    }
}

# 5. Summary
Write-Host "`n================================================" -ForegroundColor Cyan
Write-Host "  MSIX Build Summary" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

Get-ChildItem -Path $msixOutputDir -Recurse -File -Include *.msix,*.msixbundle,*.msixupload | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  [$($_.Extension)] $($_.Name) ($sizeMb MB)" -ForegroundColor Green
    Write-Host "      Location: $($_.FullName)" -ForegroundColor Gray
}
