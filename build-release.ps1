param(
    [string]$Version = "",
    [string]$ApiKey = "",
    [switch]$Interactive = $false
)

$ErrorActionPreference = "Stop"

$rootDir = $PSScriptRoot
$distDir = Join-Path $rootDir "dist"
$publishDir = Join-Path $distDir "publish"
$timestampServer = "http://timestamp.digicert.com"

# ============================================================================
# 0. Interaccion con el usuario (si se ejecuta en modo interactivo o sin parametros)
# ============================================================================
$currentVer = "1.5.2"
$propsPath = Join-Path $rootDir "Directory.Build.props"
if (Test-Path $propsPath) {
    $propsContent = [System.IO.File]::ReadAllText($propsPath)
    $match = [regex]::Match($propsContent, '(?<=<Version>)[^<]+')
    if ($match.Success) { $currentVer = $match.Value.Trim() }
}

if ($Interactive -or [string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "========================================================================" -ForegroundColor Cyan
    Write-Host "  BlueStar - Compilador Multi-Formato (Portable + Instalador + MSIX)   " -ForegroundColor Cyan
    Write-Host "========================================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "[PASO 1/2] VERSION DE EXPORTACION" -ForegroundColor Yellow
    Write-Host "------------------------------------------------------------------------" -ForegroundColor Gray
    Write-Host "  Version actual detectada en el proyecto: " -NoNewline -ForegroundColor Gray
    Write-Host "$currentVer" -ForegroundColor Green
    $inputVer = Read-Host "  Introduce la version para exportar los binarios [ENTER para $currentVer]"
    if (![string]::IsNullOrWhiteSpace($inputVer)) {
        $Version = $inputVer.Trim()
    } else {
        $Version = $currentVer
    }
    Write-Host ""
}

# Normalizar cadenas de version:
# 3 partes: Major.Minor.Build (e.g. 1.4.6) - Utilizada por semver, tags y GitHub Releases
# 4 partes: Major.Minor.Build.Revision (e.g. 1.4.6.0) - Requerida por Windows PE / MSIX AppXManifest
$vClean = $Version.Trim().TrimStart('v').Trim()
$vParts = $vClean.Split('.')
while ($vParts.Count -lt 3) { $vParts += "0" }
$threePartVersion = $vParts[0..2] -join '.'
$rev = if ($vParts.Count -gt 3) { $vParts[3] } else { "0" }
$fourPartVersion = ($vParts[0..2] -join '.') + "." + $rev

if ($Interactive -or [string]::IsNullOrWhiteSpace($ApiKey)) {
    # Descubrir claves API grabadas / configuradas en el sistema
    $recordedKeys = [System.Collections.Generic.List[PSCustomObject]]::new()
    
    # 1. depotbox.key en la raiz
    $keyFile = Join-Path $rootDir "depotbox.key"
    if (Test-Path $keyFile) {
        Get-Content $keyFile | ForEach-Object {
            $k = $_.Trim()
            if ($k -and $k -ne "YOUR-API-KEY" -and -not ($recordedKeys | Where-Object { $_.Key -eq $k })) {
                $recordedKeys.Add([PSCustomObject]@{ Key = $k; Source = "depotbox.key (Archivo local)" })
            }
        }
    }
    
    # 2. AppData settings.json
    $appDataJson = Join-Path $env:APPDATA "BlueStar\settings.json"
    if (Test-Path $appDataJson) {
        try {
            $k = (Get-Content $appDataJson -Raw | ConvertFrom-Json).defaultApiKey
            if ($k -and $k -ne "YOUR-API-KEY" -and -not ($recordedKeys | Where-Object { $_.Key -eq $k })) {
                $recordedKeys.Add([PSCustomObject]@{ Key = $k.Trim(); Source = "%AppData%\BlueStar\settings.json" })
            }
        } catch { }
    }

    # 3. Variables de entorno
    if ($env:BLUESTAR_DEPOTBOX_API_KEY) {
        $k = $env:BLUESTAR_DEPOTBOX_API_KEY.Trim()
        if ($k -and -not ($recordedKeys | Where-Object { $_.Key -eq $k })) {
            $recordedKeys.Add([PSCustomObject]@{ Key = $k; Source = "Variable de entorno BLUESTAR_DEPOTBOX_API_KEY" })
        }
    }
    if ($env:DEPOTBOX_API_KEY) {
        $k = $env:DEPOTBOX_API_KEY.Trim()
        if ($k -and -not ($recordedKeys | Where-Object { $_.Key -eq $k })) {
            $recordedKeys.Add([PSCustomObject]@{ Key = $k; Source = "Variable de entorno DEPOTBOX_API_KEY" })
        }
    }

    Write-Host "[PASO 2/2] CLAVES DE AUTENTICACION (DEPOTBOX API KEY)" -ForegroundColor Yellow
    Write-Host "------------------------------------------------------------------------" -ForegroundColor Gray

    if ($recordedKeys.Count -gt 0) {
        Write-Host "  Claves API grabadas / detectadas en el sistema:" -ForegroundColor Gray
        for ($i = 0; $i -lt $recordedKeys.Count; $i++) {
            $idx = $i + 1
            $item = $recordedKeys[$i]
            Write-Host "    [$idx] $($item.Key)" -ForegroundColor Green -NoNewline
            Write-Host "  (Origen: $($item.Source))" -ForegroundColor DarkGray
        }
        Write-Host "    [N] Ingresar una nueva clave manualmente" -ForegroundColor Cyan
        Write-Host "    [0] Compilar sin clave (Release publica predeterminada)" -ForegroundColor Gray
        Write-Host ""

        $keyChoice = Read-Host "  Selecciona una opcion [1-$($recordedKeys.Count), N, 0] [ENTER para 1]"
        if ([string]::IsNullOrWhiteSpace($keyChoice)) { $keyChoice = "1" }

        if ($keyChoice -match '^\d+$' -and [int]$keyChoice -ge 1 -and [int]$keyChoice -le $recordedKeys.Count) {
            $ApiKey = $recordedKeys[[int]$keyChoice - 1].Key
        } elseif ($keyChoice -eq "0") {
            $ApiKey = ""
        } elseif ($keyChoice -ieq "N") {
            $ApiKey = (Read-Host "  Introduce la nueva API Key de DepotBox").Trim()
            if ($ApiKey) {
                $saveChoice = Read-Host "  Deseas guardar esta clave en depotbox.key para que aparezca grabada en el menu? [S/N] [ENTER = S]"
                if ([string]::IsNullOrWhiteSpace($saveChoice) -or $saveChoice -ieq "S") {
                    $ApiKey | Set-Content -Path (Join-Path $rootDir "depotbox.key") -Force
                    Write-Host "  [OK] Clave guardada en depotbox.key" -ForegroundColor Green
                }
            }
        } else {
            $ApiKey = $keyChoice.Trim()
        }
    } else {
        Write-Host "  No se encontraron claves grabadas previamente." -ForegroundColor Yellow
        $ApiKey = (Read-Host "  Introduce tu API Key de DepotBox [ENTER para compilar sin clave]").Trim()
        if ($ApiKey) {
            $saveChoice = Read-Host "  Deseas guardar esta clave en depotbox.key para que aparezca grabada en el menu? [S/N] [ENTER = S]"
            if ([string]::IsNullOrWhiteSpace($saveChoice) -or $saveChoice -ieq "S") {
                $ApiKey | Set-Content -Path (Join-Path $rootDir "depotbox.key") -Force
                Write-Host "  [OK] Clave guardada en depotbox.key" -ForegroundColor Green
            }
        }
    }
    Write-Host ""
}

# Definicion de nombres de artefactos exportados
$portableZip = Join-Path $distDir ('BlueStar-v' + $threePartVersion + '-Portable-win-x64.zip')
$setupExe    = Join-Path $distDir ('BlueStar-v' + $threePartVersion + '-Setup-win-x64.exe')
$msixFile    = Join-Path $distDir ('BlueStar-v' + $threePartVersion + '-win-x64.msix')

Write-Host "========================================================================" -ForegroundColor Cyan
Write-Host "  Iniciando proceso de compilacion para v$threePartVersion" -ForegroundColor Cyan
Write-Host "  Formatos objetivo: Portable (.zip) + Instalador (.exe) + MSIX (.msix)" -ForegroundColor Gray
Write-Host "========================================================================" -ForegroundColor Cyan

# ============================================================================
# 1. Sincronizacion exhaustiva de versiones para proteger el sistema de updates
# ============================================================================
Write-Host "[1/7] Sincronizando referencias de version en todo el proyecto..." -ForegroundColor Yellow

# 1.1 Directory.Build.props
if (Test-Path $propsPath) {
    $propsContent = [System.IO.File]::ReadAllText($propsPath)
    $propsContent = [regex]::Replace($propsContent, '(?<=<Version>)[^<]+', $threePartVersion)
    $propsContent = [regex]::Replace($propsContent, '(?<=<AssemblyVersion>)[^<]+', $fourPartVersion)
    $propsContent = [regex]::Replace($propsContent, '(?<=<FileVersion>)[^<]+', $fourPartVersion)
    $propsContent = [regex]::Replace($propsContent, '(?<=<InformationalVersion>)[^<]+', $threePartVersion)
    [System.IO.File]::WriteAllText($propsPath, $propsContent)
    Write-Host "  [OK] Directory.Build.props sincronizado ($threePartVersion / $fourPartVersion)" -ForegroundColor Gray
}

# 1.2 BlueStar.App.csproj
$appCsprojPath = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"
if (Test-Path $appCsprojPath) {
    $content = [System.IO.File]::ReadAllText($appCsprojPath)
    $content = [regex]::Replace($content, '(?<=<Version>)[^<]+', $threePartVersion)
    $content = [regex]::Replace($content, '(?<=<AssemblyVersion>)[^<]+', $fourPartVersion)
    $content = [regex]::Replace($content, '(?<=<FileVersion>)[^<]+', $fourPartVersion)
    [System.IO.File]::WriteAllText($appCsprojPath, $content)
    Write-Host "  [OK] BlueStar.App.csproj sincronizado" -ForegroundColor Gray
}

# 1.3 installer.iss
$issPath = Join-Path $rootDir "installer.iss"
if (Test-Path $issPath) {
    $content = [System.IO.File]::ReadAllText($issPath)
    $content = [regex]::Replace($content, '(?<=#define MyAppVersion ")[^"]+', $threePartVersion)
    [System.IO.File]::WriteAllText($issPath, $content)
    Write-Host "  [OK] installer.iss sincronizado (#define MyAppVersion $threePartVersion)" -ForegroundColor Gray
}

# 1.4 Package.appxmanifest
$manifestPath = Join-Path $rootDir "src\BlueStar.Package\Package.appxmanifest"
if (Test-Path $manifestPath) {
    $content = [System.IO.File]::ReadAllText($manifestPath)
    $content = [regex]::Replace($content, '(?<=<Identity\b[^>]*?\bVersion=")[^"]+', $fourPartVersion)
    [System.IO.File]::WriteAllText($manifestPath, $content)
    Write-Host "  [OK] Package.appxmanifest sincronizado (Version $fourPartVersion)" -ForegroundColor Gray
}

# 1.5 SplashWindow.xaml
$splashPath = Join-Path $rootDir "src\BlueStar.App\Views\SplashWindow.xaml"
if (Test-Path $splashPath) {
    $content = [System.IO.File]::ReadAllText($splashPath)
    $content = [regex]::Replace($content, '(?<=x:Name="VersionText"\s+Text=")[^"]+', $threePartVersion)
    [System.IO.File]::WriteAllText($splashPath, $content)
    Write-Host "  [OK] SplashWindow.xaml sincronizado" -ForegroundColor Gray
}

# 1.6 GitHubUpdateService.cs (actualizar fallback hardcodeado y cabecera UserAgent)
$updateServicePath = Join-Path $rootDir "src\BlueStar.Infrastructure\Update\GitHubUpdateService.cs"
if (Test-Path $updateServicePath) {
    $content = [System.IO.File]::ReadAllText($updateServicePath)
    $content = [regex]::Replace($content, 'rawCurrent \?\?=\s*new Version\(\d+,\s*\d+,\s*\d+\);', "rawCurrent ??= new Version($($vParts[0]), $($vParts[1]), $($vParts[2]));")
    $content = [regex]::Replace($content, 'BlueStar-Updater/\d+\.\d+\.\d+', "BlueStar-Updater/$threePartVersion")
    [System.IO.File]::WriteAllText($updateServicePath, $content)
    Write-Host "  [OK] GitHubUpdateService.cs sincronizado (v$threePartVersion)" -ForegroundColor Gray
}

# 1.7 ViewModels y DebugLog fallback strings
$aboutVm = Join-Path $rootDir "src\BlueStar.App\ViewModels\AboutViewModel.cs"
if (Test-Path $aboutVm) {
    $content = [System.IO.File]::ReadAllText($aboutVm)
    $content = [regex]::Replace($content, '(?<=\?\?\s*")[0-9]+\.[0-9]+\.[0-9]+(?=";)', $threePartVersion)
    [System.IO.File]::WriteAllText($aboutVm, $content)
}

$settingsVm = Join-Path $rootDir "src\BlueStar.App\ViewModels\SettingsViewModel.cs"
if (Test-Path $settingsVm) {
    $content = [System.IO.File]::ReadAllText($settingsVm)
    $content = [regex]::Replace($content, '(?<=\?\?\s*")[0-9]+\.[0-9]+\.[0-9]+(?=";)', $threePartVersion)
    [System.IO.File]::WriteAllText($settingsVm, $content)
}

$debugLog = Join-Path $rootDir "src\BlueStar.Infrastructure\Services\DebugLogService.cs"
if (Test-Path $debugLog) {
    $content = [System.IO.File]::ReadAllText($debugLog)
    $content = [regex]::Replace($content, '(?<=\?\?\s*")[0-9]+\.[0-9]+\.[0-9]+(?=";)', $threePartVersion)
    [System.IO.File]::WriteAllText($debugLog, $content)
}

# ============================================================================
# 2. Limpieza de salida previa
# ============================================================================
Write-Host "[2/7] Limpiando carpeta de salida 'dist'..." -ForegroundColor Yellow
if (Test-Path $distDir) {
    Get-ChildItem -Path $distDir | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# ============================================================================
# 3. Compilacion y publicacion .NET Self-Contained Win-x64
# ============================================================================
Write-Host "[3/7] Compilando y publicando binarios (win-x64 self-contained)..." -ForegroundColor Yellow
$projectPath = Join-Path $rootDir "src\BlueStar.App\BlueStar.App.csproj"

# Clave aleatoria de esta build para cifrar las API keys (bluestar.dat). Solo vive dentro de
# BlueStar.Infrastructure.dll (AssemblyMetadata); no se guarda en disco ni en el repositorio.
$secretsKeyBytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($secretsKeyBytes)
$secretsKey = [Convert]::ToBase64String($secretsKeyBytes)

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:Version=$threePartVersion `
    -p:AssemblyVersion=$fourPartVersion `
    -p:FileVersion=$fourPartVersion `
    -p:InformationalVersion=$threePartVersion `
    "-p:BundledSecretsKey=$secretsKey" `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Error durante 'dotnet publish'!"
    exit 1
}

# Liberar bloqueos de daemons de compilacion y limpiar simbolos de depuracion
& dotnet build-server shutdown | Out-Null
Start-Sleep -Milliseconds 500
Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# Inyectar configuracion de backend con la DepotBox API Key
$resolvedApiKey = if (![string]::IsNullOrWhiteSpace($ApiKey)) { $ApiKey.Trim() } else { $null }

if ($resolvedApiKey) {
    $maskedKey = if ($resolvedApiKey.Length -gt 8) {
        $resolvedApiKey.Substring(0, 4) + "..." + $resolvedApiKey.Substring($resolvedApiKey.Length - 4)
    } else {
        "****"
    }
    Write-Host "  [DepotBox] Clave API incrustada en backend: $maskedKey" -ForegroundColor Green
} else {
    Write-Host "  [DepotBox] Sin clave de backend (defaultApiKey = null)." -ForegroundColor Yellow
}

# Claves de proveedores de mods (CurseForge, Nexus Mods, mod.io, Thunderstore) y la ruta de API de mod.io.
# Se leen de modkeys.json en la raiz (ignorado por git) y las variables de entorno tienen prioridad.
# Los usuarios pueden reemplazarlas desde Ajustes si la clave integrada se satura.
$modKeys = [ordered]@{ curseForge = $null; nexusMods = $null; modIo = $null; thunderstore = $null; modIoApiUrl = $null }
$modKeysFile = Join-Path $rootDir "modkeys.json"
if (Test-Path $modKeysFile) {
    try {
        $fileKeys = Get-Content $modKeysFile -Raw | ConvertFrom-Json
        foreach ($name in @("curseForge", "nexusMods", "modIo", "thunderstore", "modIoApiUrl")) {
            $v = $fileKeys.$name
            if ($v -and $v.Trim() -and $v.Trim() -ne "YOUR-API-KEY") { $modKeys[$name] = $v.Trim() }
        }
    } catch {
        Write-Host "  [Mods] modkeys.json no es un JSON valido; se ignora." -ForegroundColor Yellow
    }
}
$modKeyEnvVars = @{ curseForge = "BLUESTAR_CURSEFORGE_API_KEY"; nexusMods = "BLUESTAR_NEXUSMODS_API_KEY"; modIo = "BLUESTAR_MODIO_API_KEY"; thunderstore = "BLUESTAR_THUNDERSTORE_API_KEY"; modIoApiUrl = "BLUESTAR_MODIO_API_URL" }
foreach ($name in @("curseForge", "nexusMods", "modIo", "thunderstore", "modIoApiUrl")) {
    $envValue = [Environment]::GetEnvironmentVariable($modKeyEnvVars[$name])
    if ($envValue -and $envValue.Trim()) { $modKeys[$name] = $envValue.Trim() }
    if ($modKeys[$name]) {
        Write-Host "  [Mods] $name incrustado." -ForegroundColor Green
    } else {
        Write-Host "  [Mods] Sin valor para $name (los usuarios pueden poner el suyo en Ajustes)." -ForegroundColor Yellow
    }
}

# Las claves NO van en settings.json (texto plano, y el instalador lo copia a AppData): se cifran en bluestar.dat
# con AES-256-CBC + HMAC-SHA256 (formato de BundledSecrets.cs) y BlueStar las descifra solo en memoria.
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

$bundledSecrets = [ordered]@{
    defaultApiKey = $resolvedApiKey
    curseForge = $modKeys["curseForge"]
    nexusMods = $modKeys["nexusMods"]
    modIo = $modKeys["modIo"]
    thunderstore = $modKeys["thunderstore"]
    modIoApiUrl = $modKeys["modIoApiUrl"]
}
$secretsPath = Join-Path $publishDir "bluestar.dat"
$encryptedSecrets = Protect-BundledSecrets ($bundledSecrets | ConvertTo-Json -Compress) $secretsKeyBytes
[System.IO.File]::WriteAllText($secretsPath, $encryptedSecrets, [System.Text.UTF8Encoding]::new($false))
Write-Host "  [Claves] Cifradas en bluestar.dat (clave por build dentro de BlueStar.Infrastructure.dll)" -ForegroundColor Green

$releaseSettings = [ordered]@{
    # "system": BlueStar usa el idioma de Windows en el primer inicio (Espanol -> es, resto -> en).
    # El instalador lo reemplaza por el idioma elegido en el asistente.
    language = "system"
    defaultApiUrl = "https://depotbox.org"
    deleteDepotsAfterInstall = $true
    showNsfwContent = $false
    showDrmContent = $true
    enableAdvancedBuildOptions = $false
    enableExperimentalMods = $false
    checkSystemRequirementsOnStartup = $true
}
$settingsJsonPath = Join-Path $publishDir "settings.json"
$releaseSettings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsJsonPath -Encoding UTF8

# Verificacion: ninguna clave puede quedar en texto plano en los archivos de texto publicados
$plainKeys = @($resolvedApiKey) + @($modKeys.Values) | Where-Object { $_ -and $_.Length -ge 8 -and $_ -notlike "https://*" }
$textFiles = Get-ChildItem -Path $publishDir -Recurse -File | Where-Object { $_.Extension -in @(".json", ".txt", ".config", ".xml", ".dat", ".ini") }
foreach ($file in $textFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    foreach ($k in $plainKeys) {
        if ($text.Contains($k)) { throw "Una API key quedo en texto plano en $($file.FullName)" }
    }
}
Write-Host "  [Claves] Verificado: ninguna clave en texto plano en la publicacion" -ForegroundColor Green

# ============================================================================
# 4. Firma de codigo de los binarios
# ============================================================================
Write-Host "[4/7] Firmando binarios con certificado 'BlueStar Devs'..." -ForegroundColor Yellow
$certSubject = "CN=BlueStar Devs"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject -or $_.Subject -like "*BlueStar*Devs*" } | Select-Object -First 1

if (-not $cert) {
    Write-Host "  Creando nuevo certificado de firma de codigo '$certSubject'..." -ForegroundColor Gray
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) -FriendlyName "BlueStar Developers"
}

if ($cert) {
    $filesToSign = Get-ChildItem -Path $publishDir -File | Where-Object { $_.Extension -in @(".exe", ".dll") -and ($_.Name -like "BlueStar*" -or $_.Extension -eq ".exe") }
    foreach ($file in $filesToSign) {
        try {
            Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $timestampServer -HashAlgorithm SHA256 -ErrorAction SilentlyContinue | Out-Null
        } catch { }
    }
    
    $mainExe = Join-Path $publishDir "BlueStar.exe"
    if (Test-Path $mainExe) {
        $sig = Get-AuthenticodeSignature -FilePath $mainExe
        Write-Host "  Firma verificada para BlueStar.exe (Signer: $($sig.SignerCertificate.Subject))" -ForegroundColor Cyan
    }
}

# ============================================================================
# 4.5 Runtimes y prerrequisitos del sistema (descargados de Microsoft y cacheados en build-cache\)
#     - Setup/Portable: {app}\prerequisites con VC++ x64/x86 (offline) y el bootstrapper de WebView2 (~2 MB,
#       solo descarga WebView2 si falta; Windows 11 y Windows 10 actualizado ya lo traen)
#     - Portable: ademas WebView2 Fixed Version en {app}\WebView2Runtime (no requiere instalar nada)
#     - MSIX: ninguno (ver paso 7); la app los descarga solo si hacen falta
#     .NET 8 ya va incluido porque la publicacion es self-contained.
# ============================================================================
Write-Host "[4.5/7] Preparando runtimes y prerrequisitos del sistema..." -ForegroundColor Yellow
$cacheDir = Join-Path $rootDir "build-cache"
$prereqCacheDir = Join-Path $cacheDir "prerequisites"
$webView2FixedVersion = "156.0.4314.42"
$webView2FixedUrl = "https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/074c0930-36ad-4756-9d04-dfe0e503bd94/Microsoft.WebView2.FixedVersionRuntime.$webView2FixedVersion.x64.cab"
$prerequisites = [ordered]@{
    "vc_redist.x64.exe" = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
    "vc_redist.x86.exe" = "https://aka.ms/vs/17/release/vc_redist.x86.exe"
    "dxwebsetup.exe" = "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe"
    "MicrosoftEdgeWebview2Setup.exe" = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
}

function Get-MicrosoftFile([string]$Url, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Destination)) {
        Write-Host "  Descargando $(Split-Path -Leaf $Destination)..." -ForegroundColor Gray
        & curl.exe -sSL --fail -o $Destination $Url
        if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue; throw "Descarga fallida: $Url" }
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $Destination
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notlike "CN=Microsoft Corporation*") {
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        throw "Firma de Microsoft invalida en $Destination"
    }
}

New-Item -ItemType Directory -Force -Path $prereqCacheDir | Out-Null
$publishPrereqDir = Join-Path $publishDir "prerequisites"
New-Item -ItemType Directory -Force -Path $publishPrereqDir | Out-Null
foreach ($name in $prerequisites.Keys) {
    $cached = Join-Path $prereqCacheDir $name
    Get-MicrosoftFile $prerequisites[$name] $cached
    Copy-Item -LiteralPath $cached -Destination $publishPrereqDir -Force
}
Write-Host "  [OK] Instaladores copiados a prerequisites\ ($($prerequisites.Count))" -ForegroundColor Gray

$fixedCacheDir = Join-Path $cacheDir "webview2-fixed"
$fixedCab = Join-Path $fixedCacheDir "Microsoft.WebView2.FixedVersionRuntime.$webView2FixedVersion.x64.cab"
$fixedRuntimeDir = Join-Path $fixedCacheDir "x64\Microsoft.WebView2.FixedVersionRuntime.$webView2FixedVersion.x64"
New-Item -ItemType Directory -Force -Path $fixedCacheDir | Out-Null
Get-MicrosoftFile $webView2FixedUrl $fixedCab
if (-not (Test-Path -LiteralPath (Join-Path $fixedRuntimeDir "msedgewebview2.exe"))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $fixedCacheDir "x64") | Out-Null
    & expand.exe -F:* $fixedCab (Join-Path $fixedCacheDir "x64") | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $fixedRuntimeDir "msedgewebview2.exe"))) { throw "No se pudo extraer WebView2 Fixed Version" }
}
Write-Host "  [OK] WebView2 Fixed Version $webView2FixedVersion listo para el Portable" -ForegroundColor Gray

# DLLs del runtime de Visual C++ 2015-2022 para el Portable (despliegue app-local, sin instalar nada):
# x64 junto a BlueStar.exe y x86 en vcruntime\x86. Salen de la carpeta Redist oficial de Visual Studio / Build Tools.
$vswhereExe = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vcRedistRoot = $null
if (Test-Path $vswhereExe) {
    $vsInstall = & $vswhereExe -latest -products * -property installationPath
    if ($vsInstall) {
        $vcRedistRoot = Get-ChildItem (Join-Path $vsInstall "VC\Redist\MSVC") -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName "x64\Microsoft.VC143.CRT\vcruntime140.dll")) } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    }
}
if (-not $vcRedistRoot) { throw "No se encontraron las DLLs de Visual C++ (VC\Redist\MSVC) de Visual Studio / Build Tools" }
$vcCrtX64Dir = Join-Path $vcRedistRoot.FullName "x64\Microsoft.VC143.CRT"
$vcCrtX86Dir = Join-Path $vcRedistRoot.FullName "x86\Microsoft.VC143.CRT"
foreach ($dll in Get-ChildItem -Path $vcCrtX64Dir, $vcCrtX86Dir -Filter "*.dll") {
    $sig = Get-AuthenticodeSignature -LiteralPath $dll.FullName
    # Las DLLs del CRT las firma "Microsoft Windows" / "Microsoft Windows Software Compatibility Publisher"
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch '^CN=Microsoft [^,]+, O=Microsoft Corporation,') { throw "Firma de Microsoft invalida en $($dll.FullName)" }
}
Write-Host "  [OK] Runtime Visual C++ $($vcRedistRoot.Name) (x64 + x86) listo para el Portable" -ForegroundColor Gray

# ============================================================================
# 5. Formato 1: Generar archivo Portable (.zip)
# ============================================================================
Write-Host "[5/7] Generando paquete Portable (.zip)..." -ForegroundColor Yellow
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }

$portableMarkerPath = Join-Path $publishDir ".portable"
New-Item -ItemType File -Path $portableMarkerPath -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item -Path $portableMarkerPath -Force -ErrorAction SilentlyContinue

# WebView2 Fixed Version solo en el Portable (el Setup lleva el bootstrapper, que el Portable no necesita)
$zipArchive = [System.IO.Compression.ZipFile]::Open($portableZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    # Windows PowerShell 5.1 escribe las entradas con '\'; usar el mismo separador que el resto del zip
    $sep = if ($zipArchive.Entries | Where-Object { $_.FullName.Contains('/') } | Select-Object -First 1) { '/' } else { '\' }
    $webView2InstallerEntry = $zipArchive.GetEntry("prerequisites${sep}MicrosoftEdgeWebview2Setup.exe")
    if ($webView2InstallerEntry) { $webView2InstallerEntry.Delete() }
    Get-ChildItem -LiteralPath $fixedRuntimeDir -Recurse -File | ForEach-Object {
        $entryName = "WebView2Runtime$sep" + $_.FullName.Substring($fixedRuntimeDir.Length + 1).Replace('\', $sep)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    Get-ChildItem -LiteralPath $vcCrtX64Dir -Filter "*.dll" | ForEach-Object {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $_.FullName, $_.Name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    Get-ChildItem -LiteralPath $vcCrtX86Dir -Filter "*.dll" | ForEach-Object {
        $entryName = "vcruntime${sep}x86${sep}" + $_.Name
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zipArchive.Dispose()
}

$zipItem = Get-Item $portableZip
$zipMb = [math]::Round($zipItem.Length / 1048576, 2)
Write-Host "  [OK] Portable Zip generado: $portableZip ($zipMb MB)" -ForegroundColor Green

# ============================================================================
# 6. Formato 2: Generar instalador Windows (.exe) con Inno Setup
# ============================================================================
Write-Host "[6/7] Compilando Instalador de Windows con Inno Setup..." -ForegroundColor Yellow
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Users\Valen\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$isccExe = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $isccExe) {
    Write-Warning "No se encontro ISCC.exe. Por favor verifica la instalacion de Inno Setup 6."
} else {
    & $isccExe "/Qp" ('/DMyAppVersion=' + $threePartVersion) $issPath

    if ($LASTEXITCODE -ne 0) {
        Write-Error "La compilacion con Inno Setup fallo!"
        exit 1
    }

    if (Test-Path $setupExe) {
        if ($cert) {
            Write-Host "  Firmando ejecutable del instalador con timestamp..." -ForegroundColor Gray
            Set-AuthenticodeSignature -FilePath $setupExe -Certificate $cert -TimestampServer $timestampServer -HashAlgorithm SHA256 -ErrorAction SilentlyContinue | Out-Null
        }
        $setupItem = Get-Item $setupExe
        $setupMb = [math]::Round($setupItem.Length / 1048576, 2)
        Write-Host "  [OK] Instalador Setup generado: $setupExe ($setupMb MB)" -ForegroundColor Green
    }
}

# ============================================================================
# 7. Formato 3: Generar paquete MSIX (.msix)
# ============================================================================
Write-Host "[7/7] Empaquetando distribucion MSIX (Windows App Package)..." -ForegroundColor Yellow
$msixScript = Join-Path $rootDir "src\BlueStar.Package\build-msix.ps1"
if (Test-Path $msixScript) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $msixScript -Target LocalTest -Version $threePartVersion -Configuration Release -SkipPublish
    
    $generatedMsix = Get-ChildItem -Path (Join-Path $distDir "msix") -Filter "*.msix" -Recurse | Select-Object -First 1
    if ($generatedMsix) {
        Copy-Item -Path $generatedMsix.FullName -Destination $msixFile -Force
        $msixMb = [math]::Round((Get-Item $msixFile).Length / 1048576, 2)
        Write-Host "  [OK] Paquete MSIX generado: $msixFile ($msixMb MB)" -ForegroundColor Green
    } else {
        Write-Warning "No se encontro el archivo .msix generado en dist\msix."
    }
} else {
    Write-Warning "No se encontro el script build-msix.ps1 en src\BlueStar.Package."
}

# ============================================================================
# Resumen y Verificacion de Integridad de Artefactos Exportados
# ============================================================================
Write-Host ""
Write-Host "========================================================================" -ForegroundColor Cyan
Write-Host "  Compilados generados exitosamente! (v$threePartVersion)               " -ForegroundColor Green
Write-Host "========================================================================" -ForegroundColor Cyan

Get-ChildItem -Path $distDir -File | Where-Object { $_.Extension -in @(".zip", ".exe", ".msix") } | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
    [PSCustomObject]@{
        Formato = switch ($_.Extension) {
            ".zip"  { "Portable (.zip)" }
            ".exe"  { "Instalador (.exe)" }
            ".msix" { "Paquete MSIX (.msix)" }
            default { $_.Extension }
        }
        Archivo = $_.Name
        TamanoMB = [math]::Round($_.Length / 1MB, 2)
        SHA256 = $hash
    }
} | Format-Table -AutoSize
