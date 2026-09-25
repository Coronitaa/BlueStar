param(
    [string]$Version = "1.4.1"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  BlueStar Release Builder (v$Version)   " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$rootDir = $PSScriptRoot
$distDir = Join-Path $rootDir "dist"
$publishDir = Join-Path $distDir "publish"
$portableZip = Join-Path $distDir ('BlueStar-v' + $Version + '-Portable-win-x64.zip')
$setupExe = Join-Path $distDir ('BlueStar-v' + $Version + '-Setup-win-x64.exe')
$timestampServer = "http://timestamp.digicert.com"

# 1. Clean previous dist output
if (Test-Path $distDir) {
    Write-Host "[1/6] Cleaning previous dist output..." -ForegroundColor Yellow
    Get-ChildItem -Path $distDir | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# 2. Publish Self-Contained Win-x64
Write-Host "[2/6] Publishing self-contained win-x64 release build..." -ForegroundColor Yellow
$projectPath = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=false -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 2.1 Inject bundled configuration and API keys into publish directory (dynamically retrieved, NEVER committed to git)
Write-Host "  Configuring bundled release settings and API key from local environment..." -ForegroundColor Gray
$appDataSettingsPath = Join-Path $env:APPDATA "BlueStar\settings.json"
$discoveredApiKey = $null
$discoveredApiUrl = "https://depotbox.org"

if (Test-Path $appDataSettingsPath) {
    try {
        $existingConfig = Get-Content $appDataSettingsPath -Raw | ConvertFrom-Json
        if ($existingConfig.defaultApiKey -and $existingConfig.defaultApiKey -ne "YOUR-API-KEY") {
            $discoveredApiKey = $existingConfig.defaultApiKey
        }
        if ($existingConfig.defaultApiUrl) {
            $discoveredApiUrl = $existingConfig.defaultApiUrl
        }
    } catch { }
}

if ([string]::IsNullOrWhiteSpace($discoveredApiKey)) {
    $discoveredApiKey = $env:BLUESTAR_DEPOTBOX_API_KEY
    if ([string]::IsNullOrWhiteSpace($discoveredApiKey)) {
        $discoveredApiKey = $env:DEPOTBOX_API_KEY
    }
}

$releaseSettings = [ordered]@{
    language = "en"
    defaultApiUrl = $discoveredApiUrl
    defaultApiKey = $discoveredApiKey
    deleteDepotsAfterInstall = $true
    showNsfwContent = $false
    showDrmContent = $true
    enableAdvancedBuildOptions = $false
    enableExperimentalMods = $false
    checkSystemRequirementsOnStartup = $true
}
$settingsJsonPath = Join-Path $publishDir "settings.json"
$releaseSettings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsJsonPath -Encoding UTF8

# 2.2 Bundle Visual C++, DirectX, and Windows system runtime dependencies for zero-dependency portable mode
Write-Host "  Bundling VC++, DirectX runtimes and system dependencies for zero-dependency execution..." -ForegroundColor Gray
$sys32 = "$env:WINDIR\System32"
$systemDlls = @(
    "msvcp140.dll",
    "msvcp140_1.dll",
    "msvcp140_2.dll",
    "msvcp140_atomic_wait.dll",
    "msvcp140_codecvt_ids.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "vcruntime140_threads.dll",
    "vcomp140.dll",
    "ucrtbase.dll",
    "d3dcompiler_47.dll",
    "d3dcompiler_43.dll",
    "d3dx9_43.dll",
    "d3dx11_43.dll",
    "xaudio2_7.dll",
    "xinput1_3.dll",
    "xinput1_4.dll"
)
$toolsDir = Join-Path $publishDir "tools"
foreach ($dll in $systemDlls) {
    $srcPath = Join-Path $sys32 $dll
    if (Test-Path $srcPath) {
        Copy-Item -Path $srcPath -Destination $publishDir -Force
        if (Test-Path $toolsDir) {
            Copy-Item -Path $srcPath -Destination $toolsDir -Force
        }
    }
}

# 2.3 Copy complete .NET host runtime and core libraries to tools directory for DepotDownloaderMod standalone operation
Write-Host "  Bundling complete .NET host runtime and dependencies for DepotDownloaderMod standalone execution..." -ForegroundColor Gray
$runtimeDlls = Get-ChildItem -Path $publishDir -Filter "*.dll" | Where-Object {
    $_.Name -like "System.*.dll" -or
    $_.Name -like "Microsoft.Win32.*.dll" -or
    $_.Name -like "Microsoft.Extensions.*.dll" -or
    $_.Name -in @("coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "mscorlib.dll", "netstandard.dll")
}
foreach ($rDll in $runtimeDlls) {
    Copy-Item -Path $rDll.FullName -Destination $toolsDir -Force
}

# Ensure DepotDownloaderMod.runtimeconfig.json is self-contained aware
$ddmRuntimeConfig = Join-Path $toolsDir "DepotDownloaderMod.runtimeconfig.json"
$ddmConfigContent = @{
    runtimeOptions = @{
        tfm = "net8.0"
        rollForward = "LatestMajor"
        includedFrameworks = @(
            @{
                name = "Microsoft.NETCore.App"
                version = "8.0.0"
            }
        )
        configProperties = @{
            "System.Globalization.Invariant" = $true
            "System.Globalization.PredefinedCulturesOnly" = $true
            "System.Reflection.Metadata.MetadataUpdater.IsSupported" = $false
            "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization" = $false
        }
    }
}
$ddmConfigContent | ConvertTo-Json -Depth 5 | Set-Content -Path $ddmRuntimeConfig -Encoding UTF8

# 3. Setup Code Signing Certificate ("BlueStar Devs")
Write-Host "[3/6] Configuring code signing certificate ('BlueStar Devs')..." -ForegroundColor Yellow
$certSubject = "CN=BlueStar Devs"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject -or $_.Subject -like "*BlueStar*Devs*" } | Select-Object -First 1

if (-not $cert) {
    Write-Host "  Creating new Code Signing Certificate '$certSubject'..." -ForegroundColor Gray
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) -FriendlyName "BlueStar Developers"
}

if ($cert) {
    Write-Host "  Using Certificate: $($cert.Subject) [Thumbprint: $($cert.Thumbprint)]" -ForegroundColor Green
    
    # Sign main executable and key libraries
    Write-Host "  Signing application binaries with DigiCert timestamp..." -ForegroundColor Gray
    $filesToSign = Get-ChildItem -Path $publishDir -Include *.exe, *.dll -Recurse
    foreach ($file in $filesToSign) {
        try {
            Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $timestampServer -HashAlgorithm SHA256 -ErrorAction SilentlyContinue | Out-Null
        } catch { }
    }
    
    $mainExe = Join-Path $publishDir "BlueStar.exe"
    if (Test-Path $mainExe) {
        $sig = Get-AuthenticodeSignature -FilePath $mainExe
        Write-Host "  Verification for BlueStar.exe: $($sig.Status) (Signer: $($sig.SignerCertificate.Subject), Timestamp: $($sig.TimeStamperCertificate -ne $null))" -ForegroundColor Cyan
    }
} else {
    Write-Warning "Could not obtain code signing certificate for '$certSubject'."
}

# 4. Create Portable Zip
Write-Host "[4/6] Packaging Portable Zip archive..." -ForegroundColor Yellow
if (Test-Path $portableZip) {
    Remove-Item $portableZip -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipItem = Get-Item $portableZip
$zipMb = [math]::Round($zipItem.Length / 1048576, 2)
Write-Host "  Portable Zip created: $portableZip ($zipMb MB)" -ForegroundColor Green

# 5. Compile Inno Setup Installer
Write-Host "[5/6] Compiling Windows Installer with Inno Setup..." -ForegroundColor Yellow
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Users\Valen\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
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
        # Sign the generated installer executable with RFC 3161 Timestamp
        if ($cert) {
            Write-Host "  Signing Installer executable ($setupExe) with DigiCert timestamp..." -ForegroundColor Gray
            Set-AuthenticodeSignature -FilePath $setupExe -Certificate $cert -TimestampServer $timestampServer -HashAlgorithm SHA256 -ErrorAction SilentlyContinue | Out-Null
            $setupSig = Get-AuthenticodeSignature -FilePath $setupExe
            Write-Host "  Verification for Installer: $($setupSig.Status) (Signer: $($setupSig.SignerCertificate.Subject), Timestamp: $($setupSig.TimeStamperCertificate -ne $null))" -ForegroundColor Cyan
        }
        
        $setupItem = Get-Item $setupExe
        $setupMb = [math]::Round($setupItem.Length / 1048576, 2)
        Write-Host "  Setup Installer created: $setupExe ($setupMb MB)" -ForegroundColor Green
    }
}

# 6. Summary and Artifact Verification
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Release artifacts generated successfully! " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan

Get-ChildItem -Path $distDir -File | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
    [PSCustomObject]@{
        Name = $_.Name
        SizeMB = [math]::Round($_.Length / 1MB, 2)
        SHA256 = $hash
    }
} | Format-Table -AutoSize
