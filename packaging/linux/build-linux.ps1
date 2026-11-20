param(
    [string]$Version = "1.6.4",
    [string]$ApiKey = "5d5e7161-0368-439a-8bc6-764b9a254c07"
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$distDir = Join-Path $rootDir "dist"
$linuxOutDir = Join-Path $distDir "linux"
$appDir = Join-Path $linuxOutDir "BlueStar.AppDir"
$libDir = Join-Path $appDir "usr\lib\bluestar"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  BlueStar Linux Package Builder (v$Version)   " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

if (Test-Path $linuxOutDir) {
    Remove-Item $linuxOutDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $libDir -Force | Out-Null

# 1. Random key for bluestar.dat
$secretsKeyBytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($secretsKeyBytes)
$secretsKey = [Convert]::ToBase64String($secretsKeyBytes)

# 2. Publish .NET self-contained linux-x64
Write-Host "[1/4] Publicando binarios Linux (linux-x64 self-contained)..." -ForegroundColor Yellow
$projectPath = Join-Path $rootDir "src\BlueStar.Linux\BlueStar.Linux.csproj"
& dotnet publish $projectPath `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    "-p:BundledSecretsKey=$secretsKey" `
    -o $libDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish linux-x64 falló!"
    exit 1
}

# Limpiar archivos .pdb
Get-ChildItem -Path $libDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. Inyectar secretos en bluestar.dat
Write-Host "[2/4] Cifrando claves API en bluestar.dat..." -ForegroundColor Yellow
function Protect-BundledSecrets([string]$Json, [byte[]]$Key) {
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = [byte[]]$Key[0..31]
        $aes.GenerateIV()
        $plain = [System.Text.Encoding]::UTF8.GetBytes($Json)
        $cipher = $aes.CreateEncryptor().TransformFinalBlock($plain, 0, $plain.Length)
        $signed = [byte[]]($aes.IV + $cipher)
        $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (,[byte[]]$Key[32..63])
        try { $mac = $hmac.ComputeHash($signed) } finally { $hmac.Dispose() }
        return [Convert]::ToBase64String([byte[]]($signed + $mac))
    } finally {
        $aes.Dispose()
    }
}

$modKeys = [ordered]@{ curseForge = $null; nexusMods = $null; modIo = $null; thunderstore = $null; modIoApiUrl = $null }
$modKeysFile = Join-Path $rootDir "modkeys.json"
if (Test-Path $modKeysFile) {
    try {
        $fileKeys = Get-Content $modKeysFile -Raw | ConvertFrom-Json
        foreach ($name in @("curseForge", "nexusMods", "modIo", "thunderstore", "modIoApiUrl")) {
            $v = $fileKeys.$name
            if ($v -and $v.Trim() -and $v.Trim() -ne "YOUR-API-KEY") { $modKeys[$name] = $v.Trim() }
        }
    } catch { }
}

$bundledSecrets = [ordered]@{
    defaultApiKey = $ApiKey
    curseForge = $modKeys["curseForge"]
    nexusMods = $modKeys["nexusMods"]
    modIo = $modKeys["modIo"]
    thunderstore = $modKeys["thunderstore"]
    modIoApiUrl = $modKeys["modIoApiUrl"]
}
$secretsPath = Join-Path $libDir "bluestar.dat"
$encryptedSecrets = Protect-BundledSecrets ($bundledSecrets | ConvertTo-Json -Compress) $secretsKeyBytes
[System.IO.File]::WriteAllText($secretsPath, $encryptedSecrets, [System.Text.UTF8Encoding]::new($false))

# 4. Packaging in WSL (tar.gz + AppImage)
Write-Host "[3/4] Empaquetando tar.gz y AppImage con WSL..." -ForegroundColor Yellow
$wslRoot = "/mnt/c" + ($rootDir.Substring(2) -replace '\\', '/')

$wslScript = @"
set -e
cd "$wslRoot"
chmod +x "dist/linux/BlueStar.AppDir/usr/lib/bluestar/BlueStar" || true
find "dist/linux/BlueStar.AppDir/usr/lib/bluestar/tools" -name "*.sh" -exec chmod +x {} + 2>/dev/null || true

# Generar tar.gz
tar -C "dist/linux/BlueStar.AppDir/usr/lib" -czf "dist/linux/BlueStar-v${Version}-linux-x64.tar.gz" bluestar

# Preparar AppDir
cp packaging/linux/AppRun dist/linux/BlueStar.AppDir/AppRun
chmod +x dist/linux/BlueStar.AppDir/AppRun
cp packaging/linux/bluestar.desktop dist/linux/BlueStar.AppDir/bluestar.desktop
cp src/BlueStar.App/Assets/bluestar.png dist/linux/BlueStar.AppDir/bluestar.png

# Descargar appimagetool si no existe
TMP_WORK=`$(mktemp -d)
TOOL="`$TMP_WORK/appimagetool"
curl -fsSL -o "`$TOOL" "https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage"
chmod +x "`$TOOL"

ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "`$TOOL" --no-appstream dist/linux/BlueStar.AppDir "dist/linux/BlueStar-v${Version}-linux-x64.AppImage"
rm -rf "`$TMP_WORK"

cd dist/linux
sha256sum BlueStar-v${Version}-linux-x64.tar.gz BlueStar-v${Version}-linux-x64.AppImage > SHA256SUMS
"@

$wslScriptUtf8 = [System.Text.Encoding]::UTF8.GetBytes($wslScript)
$tempScript = Join-Path $linuxOutDir "pack.sh"
[System.IO.File]::WriteAllBytes($tempScript, $wslScriptUtf8)

$wslTempScript = "$wslRoot/dist/linux/pack.sh"
& wsl bash -c "tr -d '\r' < '$wslTempScript' | bash"

if (Test-Path $tempScript) { Remove-Item $tempScript -Force }

Write-Host "[4/4] Verificando artefactos Linux generados..." -ForegroundColor Green
Get-ChildItem -Path $linuxOutDir -File | Where-Object { $_.Extension -in @(".gz", ".AppImage") -or $_.Name -eq "SHA256SUMS" } | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name) ($sizeMb MB)" -ForegroundColor Green
}
