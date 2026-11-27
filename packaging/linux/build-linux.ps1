param(
    [string]$Version = "1.6.6",
    [string]$ApiKey = "",
    [ValidateSet("linux-x64", "linux-arm64")]
    [string]$Rid = "linux-x64"
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$distDir = Join-Path $rootDir "dist"
$linuxOutDir = Join-Path $distDir "linux"
$appDir = Join-Path $linuxOutDir "BlueStar.AppDir"
$libDir = Join-Path $appDir "usr\lib\bluestar"

# Resolver clave API de DepotBox desde parametro o depotkey.json
$resolvedApiKey = $ApiKey
$depotKeyFile = Join-Path $rootDir "depotkey.json"
if (Test-Path $depotKeyFile) {
    try {
        $depotJson = Get-Content $depotKeyFile -Raw | ConvertFrom-Json
        if ($depotJson.DepotBox -and $depotJson.DepotBox.Trim()) {
            $resolvedApiKey = $depotJson.DepotBox.Trim()
        }
    } catch { }
}
if (-not $resolvedApiKey) {
    $resolvedApiKey = "5d5e7161-0368-439a-8bc6-764b9a254c07"
}

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  BlueStar Linux Package Builder (v$Version)   " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

if (Test-Path $linuxOutDir) {
    Remove-Item $linuxOutDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $libDir -Force | Out-Null

# Limpiar bin y obj para garantizar regeneracion de AssemblyMetadata con la clave de build
Write-Host "  Limpiando bin y obj de proyectos para regenerar AssemblyMetadata..." -ForegroundColor Cyan
Get-ChildItem -Path (Join-Path $rootDir "src") -Include "bin", "obj" -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike "*\tools\*" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 1. Random key for bluestar.dat
$secretsKeyBytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($secretsKeyBytes)
$secretsKey = [Convert]::ToBase64String($secretsKeyBytes)

# 2. Publish .NET self-contained
Write-Host "[1/4] Publicando binarios Linux ($Rid self-contained)..." -ForegroundColor Yellow
$projectPath = Join-Path $rootDir "src\BlueStar.Linux\BlueStar.Linux.csproj"
& dotnet publish $projectPath `
    -c Release `
    -r $Rid `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    "-p:BundledSecretsKey=$secretsKey" `
    -o $libDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish $Rid falló!"
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
    defaultApiKey = $resolvedApiKey
    curseForge = $modKeys["curseForge"]
    nexusMods = $modKeys["nexusMods"]
    modIo = $modKeys["modIo"]
    thunderstore = $modKeys["thunderstore"]
    modIoApiUrl = $modKeys["modIoApiUrl"]
}
$secretsPath = Join-Path $libDir "bluestar.dat"
$encryptedSecrets = Protect-BundledSecrets ($bundledSecrets | ConvertTo-Json -Compress) $secretsKeyBytes
[System.IO.File]::WriteAllText($secretsPath, $encryptedSecrets, [System.Text.UTF8Encoding]::new($false))

# Verificacion de clave en AssemblyInfo
$infraAssemblyInfo = Get-ChildItem -Path (Join-Path $rootDir "src\BlueStar.Infrastructure\obj") -Recurse -Filter "*AssemblyInfo.cs" | Select-Object -First 1
if ($infraAssemblyInfo) {
    $infoContent = Get-Content $infraAssemblyInfo.FullName -Raw
    $pattern = 'AssemblyMetadata\("BundledSecretsKey",\s*\(?([^);]+)\)?\)'
    if ($infoContent -match $pattern) {
        $rawGroup = $matches[1]
        $parts = [regex]::Matches($rawGroup, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }
        $extractedKey = -join $parts
        if ($extractedKey -ne $secretsKey) {
            throw "ERROR CRITICO: La clave de build no coincide con AssemblyInfo ($extractedKey vs $secretsKey)!"
        }
    } else {
        throw "ERROR CRITICO: No se encontro AssemblyMetadata para BundledSecretsKey en $($infraAssemblyInfo.FullName)!"
    }
}

# Verificacion de descifrado
function Test-BundledSecretsDecryption([string]$DatPath, [byte[]]$Key) {
    $data = [Convert]::FromBase64String([System.IO.File]::ReadAllText($DatPath).Trim())
    if ($data.Length -lt 64) { throw "bluestar.dat truncado" }
    $iv = [byte[]]$data[0..15]
    $cipher = [byte[]]$data[16..($data.Length - 33)]
    $signed = [byte[]]$data[0..($data.Length - 33)]
    $mac = [byte[]]$data[($data.Length - 32)..($data.Length - 1)]

    $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (,[byte[]]$Key[32..63])
    try {
        $computedMac = $hmac.ComputeHash($signed)
        if (![System.Linq.Enumerable]::SequenceEqual([byte[]]$mac, [byte[]]$computedMac)) {
            throw "El HMAC de bluestar.dat no coincide con la clave de compilacion!"
        }
    } finally { $hmac.Dispose() }

    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = [byte[]]$Key[0..31]
        $plain = $aes.CreateDecryptor($aes.Key, $iv).TransformFinalBlock($cipher, 0, $cipher.Length)
        $json = [System.Text.Encoding]::UTF8.GetString($plain)
        return $json | ConvertFrom-Json
    } finally { $aes.Dispose() }
}

$testDecrypted = Test-BundledSecretsDecryption $secretsPath $secretsKeyBytes
if ($resolvedApiKey -and $testDecrypted.defaultApiKey -ne $resolvedApiKey) {
    throw "Verificacion fallida: la clave descifrada ($($testDecrypted.defaultApiKey)) no coincide con `$resolvedApiKey ($resolvedApiKey)!"
}
Write-Host "  [Claves] Verificado: descifrado correcto de bluestar.dat y coincidencia de clave" -ForegroundColor Green

# 4. Packaging in WSL (tar.gz + AppImage)
Write-Host "[3/4] Empaquetando tar.gz y AppImage con WSL..." -ForegroundColor Yellow
$wslRoot = "/mnt/c" + ($rootDir.Substring(2) -replace '\\', '/')
$packWslPath = "$wslRoot/packaging/linux/pack_wsl.sh"
$arch = if ($Rid -eq "linux-arm64") { "arm64" } else { "x86_64" }
& wsl bash -c "tr -d '\r' < '$packWslPath' | ROOT_DIR='$wslRoot' RID='$Rid' ARCH='$arch' VERSION='$Version' bash"

Write-Host "[4/4] Verificando artefactos Linux generados..." -ForegroundColor Green
Get-ChildItem -Path $linuxOutDir -File | Where-Object { $_.Extension -in @(".gz", ".AppImage", ".asc") -or $_.Name -eq "SHA256SUMS" } | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name) ($sizeMb MB)" -ForegroundColor Green
}
