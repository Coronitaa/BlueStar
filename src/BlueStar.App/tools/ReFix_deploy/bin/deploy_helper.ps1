param(
    [string]$TargetDir = "",
    [string]$BinDir = "",
    [string]$ExeDir = "",
    [string]$EngineType = "Unity",
    [string]$OnlineMode = "valve",
    [string]$PhotonAppId = "",
    [string]$PhotonRegion = "",
    [string]$GameName = "",
    [string]$UserName = "",
    [string]$RealAppId = "480",
    [string]$MaskAppId = "480",
    [string]$Language = "english",
    [string]$DLCs = "",
    [string]$DLCMode = "all",
    [string]$ListenPort = "47584",
    [string]$CustomBroadcasts = "",
    [string]$AppIdRealtime = "",
    [string]$AppIdFusion = "",
    [string]$AppIdVoice = "",
    [string]$ArbitrationMode = "auto",
    [string]$DefaultRegion = "sa"
)

# Fallback to environment variables if parameters were omitted or empty
if (-not $TargetDir -and $env:REFIX_TARGET_DIR) { $TargetDir = $env:REFIX_TARGET_DIR }
if (-not $BinDir    -and $env:REFIX_BIN_DIR)    { $BinDir    = $env:REFIX_BIN_DIR }
if (-not $ExeDir    -and $env:REFIX_EXE_DIR)    { $ExeDir    = $env:REFIX_EXE_DIR }
if (-not $EngineType -and $env:REFIX_ENGINE_TYPE) { $EngineType = $env:REFIX_ENGINE_TYPE }
if (-not $OnlineMode -and $env:REFIX_ONLINE_MODE) { $OnlineMode = $env:REFIX_ONLINE_MODE }
if (-not $GameName  -and $env:REFIX_GAME_NAME)  { $GameName  = $env:REFIX_GAME_NAME }
if (-not $UserName  -and $env:REFIX_USERNAME)   { $UserName  = $env:REFIX_USERNAME }
if ((-not $RealAppId -or $RealAppId -eq "480") -and $env:REFIX_REAL_APPID) { $RealAppId = $env:REFIX_REAL_APPID }
if ((-not $MaskAppId -or $MaskAppId -eq "480") -and $env:REFIX_MASK_APPID) { $MaskAppId = $env:REFIX_MASK_APPID }
if ((-not $ListenPort -or $ListenPort -eq "47584") -and $env:REFIX_LAN_PORT) { $ListenPort = $env:REFIX_LAN_PORT }
if (-not $DLCMode   -and $env:REFIX_DLC_MODE)   { $DLCMode   = $env:REFIX_DLC_MODE }
if (-not $DLCs      -and $env:REFIX_DLCS)       { $DLCs      = $env:REFIX_DLCS }
if (-not $CustomBroadcasts -and $env:REFIX_CUSTOM_BROADCASTS) { $CustomBroadcasts = $env:REFIX_CUSTOM_BROADCASTS }
if (-not $PhotonAppId -and $env:REFIX_PHOTON_APPID) { $PhotonAppId = $env:REFIX_PHOTON_APPID }
if (-not $PhotonRegion -and $env:REFIX_PHOTON_REGION) { $PhotonRegion = $env:REFIX_PHOTON_REGION }

# Clean paths by trimming whitespace and trailing quotes/slashes
if ($TargetDir) { $TargetDir = $TargetDir.Trim().Trim('"').TrimEnd('\').TrimEnd('/') }
if ($BinDir)    { $BinDir    = $BinDir.Trim().Trim('"').TrimEnd('\').TrimEnd('/') }
if ($ExeDir)    { $ExeDir    = $ExeDir.Trim().Trim('"').TrimEnd('\').TrimEnd('/') }
if (-not $ExeDir) { $ExeDir  = $TargetDir }

if (-not (Test-Path -LiteralPath $TargetDir)) {
    Write-Host "[ERROR] Target directory does not exist: $TargetDir" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $BinDir)) {
    Write-Host "[ERROR] Binary directory does not exist: $BinDir" -ForegroundColor Red
    exit 1
}

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host " [ReFix Deploy Engine] Target: $TargetDir" -ForegroundColor Cyan
Write-Host " [ReFix Deploy Engine] ExeDir: $ExeDir" -ForegroundColor Cyan
Write-Host " [ReFix Deploy Engine] Mode:   $OnlineMode ($EngineType)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ------------------------------------------------------------
# Helper: Persistent Identity Generator & Synchronizer
# ------------------------------------------------------------
function Get-Or-Generate-Identity {
    param(
        [string]$ConfigPath,
        [string]$InputName,
        [string]$BasePath
    )

    $finalSteamId = ""
    $finalName = ""
    $autoGenerateSteamId = $true

    # Check if ReFix.ini exists and already has persistent identity configured
    if (Test-Path -LiteralPath $ConfigPath) {
        $iniLines = Get-Content -LiteralPath $ConfigPath -ErrorAction SilentlyContinue
        foreach ($line in $iniLines) {
            if ($line -match "^\s*AutoGenerateSteamId\s*=\s*(false|0)\s*$") {
                $autoGenerateSteamId = $false
            }
            if ($line -match "^\s*SteamId\s*=\s*([0-9]{15,20})\s*$") {
                $candidate = $Matches[1].Trim()
                if ($candidate -ne "0" -and $candidate.Length -ge 15) {
                    $finalSteamId = $candidate
                }
            }
            if ($line -match "^\s*Name\s*=\s*(.+)$") {
                $n = $Matches[1].Trim()
                if ($n -and $n -ne "Player") {
                    $finalName = $n
                }
            }
        }
    }

    # If an explicit name was provided during deployment, prioritize it
    if ($InputName -and $InputName.Trim() -ne "") {
        $finalName = $InputName.Trim()
    }

    # Generate machine-unique SteamID if AutoGenerateSteamId is true or SteamId is missing
    if ($autoGenerateSteamId -or (-not $finalSteamId)) {
        $rawString = ""
        try {
            $guid = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Cryptography" -Name "MachineGuid" -ErrorAction SilentlyContinue).MachineGuid
            if ($guid) { $rawString += $guid }
        } catch {}
        if (-not $rawString) {
            $rawString += $env:COMPUTERNAME + "_" + $env:USERNAME
        }

        $md5 = [System.Security.Cryptography.MD5]::Create()
        $hashBytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($rawString))
        $accountId = [System.BitConverter]::ToUInt32($hashBytes, 0)
        $accountId = ($accountId -band 0x1FFFFFFF) + 100000000
        $basePrefix = [uint64]76561197960265728
        $generatedSteamId = $basePrefix + [uint64]$accountId
        $finalSteamId = "$generatedSteamId"

        Write-Host "  [IDENTITY] Machine-Unique SteamID64: $finalSteamId (AutoGenerate=true)" -ForegroundColor Yellow
    } else {
        Write-Host "  [IDENTITY] Preserving manual SteamID64: $finalSteamId (AutoGenerate=false)" -ForegroundColor Green
    }

    if (-not $finalName -or $finalName -eq "Player") {
        $shortHex = ($finalSteamId.Substring($finalSteamId.Length - 4))
        $finalName = "Player_$shortHex"
        Write-Host "  [IDENTITY] Assigned LAN persona name: $finalName" -ForegroundColor Yellow
    } else {
        Write-Host "  [IDENTITY] Persona Name: $finalName" -ForegroundColor Green
    }

    return @{
        SteamId             = $finalSteamId
        Name                = $finalName
        AutoGenerateSteamId = if ($autoGenerateSteamId) { "true" } else { "false" }
    }
}

# ============================================================
# ------------------------------------------------------------
# Helper: UNAE Network Topology Detection Engine (PowerShell)
# ------------------------------------------------------------
function Detect-UNAE-Topology {
    param([string]$TargetDir)

    $hasPhotonRealtime = (Get-ChildItem -LiteralPath $TargetDir -Filter "PhotonRealtime.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasPhoton3Unity   = (Get-ChildItem -LiteralPath $TargetDir -Filter "Photon3Unity3D.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasPhotonVoice    = (Get-ChildItem -LiteralPath $TargetDir -Filter "PhotonVoice.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasPhotonFusion   = (Get-ChildItem -LiteralPath $TargetDir -Filter "Fusion.Runtime.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasNanosockets    = (Get-ChildItem -LiteralPath $TargetDir -Filter "nanosockets.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasMirror         = (Get-ChildItem -LiteralPath $TargetDir -Filter "Mirror.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasKcp            = (Get-ChildItem -LiteralPath $TargetDir -Filter "kcp2k.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
    $hasEOS            = (Get-ChildItem -LiteralPath $TargetDir -Filter "*EOSSDK*.dll" -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0

    $il2cppMeta = Get-ChildItem -LiteralPath $TargetDir -Filter "global-metadata.dat" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    $il2cppPUN = $false
    $il2cppFusion = $false
    if ($il2cppMeta) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($il2cppMeta.FullName)
            $contentStr = [System.Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min($bytes.Length, 4194304))
            if ($contentStr -match "Photon\.Pun|ConnectToRegionMaster|PhotonHandler") { $il2cppPUN = $true }
            if ($contentStr -match "Fusion\.NetworkRunner|Fusion\.Runtime") { $il2cppFusion = $true }
            if ($contentStr -match "Photon\.Voice|VoiceClient") { $hasPhotonVoice = $true }
        } catch {}
    }

    # Topologia B: Multiplexado Hibrido (Fusion + nanosockets, Mirror + EOS WebRTC)
    # Se prioriza sobre Topologia C para evitar que juegos hibridos con voz auxiliar
    # (como Dumb Ways to Build) sean erróneamente clasificados como Cloud Relay estricto.
    if ($hasPhotonFusion -or $il2cppFusion -or $hasNanosockets -or $hasMirror -or $hasKcp -or $hasEOS) {
        $port = if ($hasNanosockets -or $hasPhotonFusion -or $il2cppFusion) { "27015" } else { "7777" }
        return @{
            Topology = "TopologyB"
            Name = "Topologia B: Multiplexado Hibrido (EOS P2P / Mirror / Fusion)"
            Transport = "WAN: Steam SDR P2P (AppID 480) / LAN: Sockets Directos (Hold Buffer)"
            Color = "Cyan"
            DirectP2PAllowed = $true
            P2PPort = $port
        }
    } elseif ($hasPhotonRealtime -or $hasPhoton3Unity -or $il2cppPUN -or $hasPhotonVoice) {
        return @{
            Topology = "TopologyC"
            Name = "Topologia C: Cloud-Relay Estricto (PUN 2 / Photon Voice)"
            Transport = "Tier 4 Photon Cloud Relay (DRPI Preservacion Dinamica)"
            Color = "Yellow"
            DirectP2PAllowed = $false
            P2PPort = "7777"
        }
    } else {
        return @{
            Topology = "TopologyA"
            Name = "Topologia A: Sockets Directos (P2P Puro / LAN / SDR)"
            Transport = "Tier 1 LAN Autonoma / Tier 2 WAN Steam SDR P2P"
            Color = "Green"
            DirectP2PAllowed = $true
            P2PPort = "7777"
        }
    }
}

# ------------------------------------------------------------
# Helper: Unified ReFix.ini Generator (UNAE v3.0 Specification)
# ------------------------------------------------------------
function Generate-UNAE-ReFixIni {
    param(
        [string]$IniPath,
        [hashtable]$P
    )

    $existing = @{}
    if (Test-Path $IniPath) {
        $curSec = "General"
        Get-Content $IniPath -ErrorAction SilentlyContinue | ForEach-Object {
            $line = $_.Trim()
            if ($line -match "^\[(.*)\]$") {
                $curSec = $Matches[1].Trim()
            } elseif ($line -match "^([^=;]+)=(.*)$") {
                $existing["$curSec/$($Matches[1].Trim())"] = $Matches[2].Trim()
            }
        }
    }

    $Pick = {
        param($sec, $key, $fallback)
        if ($existing.ContainsKey("$sec/$key") -and $existing["$sec/$key"] -ne "") {
            return $existing["$sec/$key"]
        }
        return $fallback
    }

    $realtimeCandidate = if ($P.AppIdRealtime) { $P.AppIdRealtime } elseif ($P.PhotonAppId) { $P.PhotonAppId } else { "" }
    $regionCandidate   = if ($P.DefaultRegion) { $P.DefaultRegion } elseif ($P.PhotonRegion) { $P.PhotonRegion } else { "sa" }

    $appIdRealtime = & $Pick "Photon" "AppIdRealtime" $realtimeCandidate
    $appIdFusion   = & $Pick "Photon" "AppIdFusion"   $P.AppIdFusion
    $appIdVoice    = & $Pick "Voice"  "AppIdVoice"    $P.AppIdVoice
    $arbMode       = & $Pick "Network" "Mode"         $P.ArbitrationMode
    $defRegion     = & $Pick "Photon" "DefaultRegion" $regionCandidate
    $bypassVal     = if ($P.DLCMode -eq "none") { "false" } else { "true" }
    $eosMode       = if ($P.OnlineMode -in @("goldberg", "offline", "lan")) { "emulated" } else { "passthrough" }
    $eosAuth       = if ($P.OnlineMode -in @("goldberg", "offline", "lan")) { "false" } else { "true" }

    $existingDiscoveryPort = if ($existing.ContainsKey("Network/DiscoveryPort") -and $existing["Network/DiscoveryPort"] -ne "") {
        $existing["Network/DiscoveryPort"]
    } elseif ($existing.ContainsKey("Network/ListenPort") -and $existing["Network/ListenPort"] -ne "") {
        $existing["Network/ListenPort"]
    } else {
        $P.ListenPort
    }

    $existingP2PPort = if ($existing.ContainsKey("P2P/P2PPort") -and $existing["P2P/P2PPort"] -ne "") {
        $existing["P2P/P2PPort"]
    } else {
        $P.P2PPort
    }

    $customBroadcasts = & $Pick "Network" "CustomBroadcasts" $P.CustomBroadcasts

    $iniContent = @"
; =============================================================================
; ReFix Universal Network Arbitration Engine (UNAE) Configuration
; =============================================================================

[Game]
GameName=$($P.GameName)
EngineType=$($P.EngineType)

[Network]
; Modo global de arbitraje:
;   auto         - Deteccion automatica de capacidades, topologia y cascada inteligente (Recomendado).
;   force_p2p    - Fuerza transporte P2P directo (Tier 1 LAN / Tier 2 SDR). Falla si el juego requiere cloud relay.
;   force_relay  - Salta P2P directo y enruta inmediatamente a traves de nubes de relevo (EOS / Photon).
;   force_lan    - Aisla la conectividad a la subred local (Tier 1 exclusivo, sin salidas a Internet).
;   offline      - Desactiva toda actividad de red externa; emulacion local absoluta.
Mode = $arbMode

; Puerto base de escucha y descubrimiento UDP para Re:Goldberg y UNAE LAN (Default: 47584)
DiscoveryPort = $existingDiscoveryPort

; Grupo de multidifusion IPv4 para descubrimiento sin configuracion en la misma subred
DiscoveryGroup = 239.255.71.84

; Tiempo de espera de respuesta de hosts locales en LAN antes de evaluar WAN (milisegundos)
LanProbeTimeoutMs = 2500

; IPs de difusion adicionales para VPNs (Radmin, Hamachi, ZeroTier) separadas por comas
CustomBroadcasts = $customBroadcasts

; Registro detallado del motor de arbitraje en ReFix.log
VerboseArbitrationLog = true


; =============================================================================
; Direct P2P & Valve Steam Datagram Relay (SDR) Tunneling
; =============================================================================
[P2P]
; Habilita los hooks de intercepcion sobre ws2_32.dll (sendto, recvfrom, connect, select, bind)
EnableWinsockHooks = true

; Enruta paquetes UDP de juegos a traves del tunel ISteamNetworking SDR de Valve (AppID 480)
EnableSteamSDR = true

; Permite la retransmision por servidores mundiales de Valve si no hay conexion P2P directa
AllowRelay = true

; Puerto UDP principal de la sesion de juego (Default: 7777 para Unreal/Mirror, 27015 para Fusion)
P2PPort = $existingP2PPort

; Tiempo de espera de negociacion de sesion P2P antes de activar fallback (milisegundos)
PeerHandshakeTimeoutMs = 5000

; Maximo porcentaje de perdida de paquetes tolerable antes de transicionar de Tier (Default: 40)
MaxPacketLossTolerance = 40


; =============================================================================
; Photon Ecosystem & Cloud Relay Configuration (PUN 2, Realtime & Fusion)
; =============================================================================
[Photon]
; Proveedor de backend para el ecosistema Photon:
;   ReFixCloud      - Infraestructura gestionada/local ReFix (Cero costo, alto rendimiento).
;   OfficialPhoton  - Conexion a la nube publica de Photon Engine (Requiere AppIDs validos).
;   CustomPhoton    - Servidores privados self-hosted (Photon Server / Luxon).
Backend = OfficialPhoton

; Clave de Aplicacion desacoplada para Photon Realtime y PUN 2 (Sincronizacion de juego y salas)
AppIdRealtime = $appIdRealtime

; Clave de Aplicacion desacoplada para Photon Fusion (Tick simulation / Prediccion de estado)
AppIdFusion = $appIdFusion

; Gestion de Regiones:
;   dynamic - Preserva fielmente la region seleccionada por el usuario en la UI del juego (Recomendado).
;   auto    - Realiza sondeo de ping y conecta automaticamente a la region de menor latencia.
;   fixed   - Fuerza estaticamente la region definida en DefaultRegion, bloqueando la UI del juego.
RegionMode = dynamic

; Region por defecto o de contingencia (sa, us, usw, use, eu, asia, jp, ru)
DefaultRegion = $defRegion

; Preserva la seleccion de region del jugador en interfaces como Phasmophobia y R.E.P.O.
PreserveGameUiRegion = true


; =============================================================================
; Positional Audio & Voice Communications Subsystem
; =============================================================================
[Voice]
; Backend de transmision de voz:
;   PhotonVoice - Utiliza el canal desacoplado Photon Voice 2 (Opus 24-48kHz).
;   EOSRTC      - Utiliza las salas de voz WebRTC de Epic Online Services.
;   SteamVoice  - Utiliza la API ISteamUser::GetVoice de Steamworks.
;   Disabled    - Desactiva el subsistema de comunicaciones por voz.
Backend = PhotonVoice

; Clave de Aplicacion desacoplada exclusiva para Photon Voice 2
; NOTA CRITICA: No reutilizar la misma clave de AppIdRealtime para evitar agotar el limite de CCU.
AppIdVoice = $appIdVoice

; Tasa de muestreo de audio / Bitrate de voz (en bps, Default: 32000)
Bitrate = 32000

; Activa la intercepcion de atenuacion espacial 3D segun la distancia entre avatares
EnableProximityVoice = true


; =============================================================================
; Emulated Steam Subsystems & User Identity
; =============================================================================
[Online]
Mode = $($P.OnlineMode)

[Steam]
MaskAppId = $($P.MaskAppId)
RealAppId = $($P.RealAppId)
Language = $($P.Language)
BypassLicenseCheck = $bypassVal
DLCs = $($P.DLCs)

[User]
Name = $($P.UserName)
AutoGenerateSteamId = $($P.AutoGenerateSteamId)
SteamId = $($P.SteamId)

[Matchmaking]
EnableLobbyFilter = false
LobbyFilterKey = game_filter
LobbyFilterValue = $($P.MaskAppId)
LobbyDistanceFilter = worldwide
MaxLobbyResults = 50

[ServerBrowser]
OverrideServerListAppId = false
ServerListAppId = 480

[Storage]
LocalSave = saves

[EOS]
Mode = $eosMode
DeviceIdAuth = $eosAuth

[Debug]
EnableLog = true
EnableConsole = false
"@
    [System.IO.File]::WriteAllText($IniPath, $iniContent)
}

# ============================================================
# ------------------------------------------------------------
# Helper: SteamStub DRM Detector & Automatic Unpacker
# ------------------------------------------------------------
function Unpack-SteamStubIfProtected {
    param(
        [string]$TargetFolder,
        [string]$ToolsBinDir
    )

    $steamlessCli = Join-Path $ToolsBinDir "tools\Steamless\Steamless.CLI.exe"
    if (-not (Test-Path $steamlessCli)) {
        return
    }

    $exeFiles = Get-ChildItem -Path $TargetFolder -Filter "*.exe" -File -Recurse -ErrorAction SilentlyContinue | 
        Where-Object { 
            $_.Name -notlike "*crashpad*" -and 
            $_.Name -notlike "*UnityCrashHandler*" -and 
            $_.Name -notlike "*.unpacked.exe" -and 
            $_.Name -notlike "*.steamstub.exe" -and 
            $_.Name -notlike "*.original.exe" 
        }

    foreach ($exe in $exeFiles) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($exe.FullName)
            if ($bytes.Length -lt 2048) { continue }
            
            $text = [System.Text.Encoding]::ASCII.GetString($bytes)
            if ($text -match "\.bind" -or $text -match "SteamDRMP\.dll") {
                Write-Host "  [SteamStub DRM] Detected Valve SteamStub protection on: $($exe.Name)" -ForegroundColor Yellow
                Write-Host "  [SteamStub DRM] Automatically unpacking with Steamless..." -ForegroundColor Cyan
                
                $backupPath = Join-Path $exe.DirectoryName "$($exe.BaseName).steamstub$($exe.Extension)"
                if (-not (Test-Path $backupPath)) {
                    Copy-Item -Path $exe.FullName -Destination $backupPath -Force
                    Write-Host "  [OK] Preserved original protected binary -> $($exe.BaseName).steamstub$($exe.Extension)" -ForegroundColor Green
                }

                $proc = Start-Process -FilePath $steamlessCli -ArgumentList "`"$($exe.FullName)`"" -WorkingDirectory $exe.DirectoryName -NoNewWindow -Wait -PassThru
                
                $unpackedFile = Join-Path $exe.DirectoryName "$($exe.Name).unpacked.exe"
                if (Test-Path $unpackedFile) {
                    Copy-Item -Path $unpackedFile -Destination $exe.FullName -Force
                    Remove-Item -Path $unpackedFile -Force
                    Write-Host "  [SUCCESS] Unpacked $($exe.Name) - SteamStub DRM removed successfully!" -ForegroundColor Green
                } else {
                    Write-Host "  [NOTICE] Binary is not packed with SteamStub DRM or is a custom engine wrapper." -ForegroundColor Yellow
                    if (Test-Path $backupPath) {
                        Remove-Item -Path $backupPath -Force
                    }
                }
            }
        } catch {
            Write-Host "  [DEBUG] Error checking SteamStub on $($exe.Name): $_"
        }
    }
}

# ============================================================
# Step 1: Scan and automatically unpack SteamStub DRM if present
# ============================================================
Write-Host "[1/6] Inspecting executables for SteamStub DRM..." -ForegroundColor Cyan
Unpack-SteamStubIfProtected -TargetFolder $TargetDir -ToolsBinDir $BinDir

# Step 2: Handle steam_api64.dll (x64) and steam_api.dll (x86) according to selected mode
# ============================================================
Write-Host "[2/6] Processing steam_api DLL instances (x64 + x86) & UNAE Network Topology..." -ForegroundColor Cyan

# Detect Network Topology via UNAE Engine Classifier
$unaeTopology = Detect-UNAE-Topology -TargetDir $TargetDir
Write-Host ""
Write-Host " ============================================================" -ForegroundColor DarkGray
Write-Host " [UNAE Engine] Clasificacion de Topologia de Red:" -ForegroundColor Cyan
Write-Host "   -> Topologia Detectada: $($unaeTopology.Name)" -ForegroundColor $($unaeTopology.Color)
Write-Host "   -> Transporte Optimo:   $($unaeTopology.Transport)" -ForegroundColor White
Write-Host "   -> Direct P2P Allowed:  $($unaeTopology.DirectP2PAllowed)" -ForegroundColor $(if ($unaeTopology.DirectP2PAllowed) { "Green" } else { "Yellow" })
Write-Host "   -> Puerto Asignado:     $($unaeTopology.P2PPort)" -ForegroundColor Cyan
Write-Host " ============================================================" -ForegroundColor DarkGray
Write-Host ""

# Discover all steam_api64.dll or backup steam_api64_valve.dll locations
$pluginDirs = @()

# For Unity games, discover dedicated Plugins folders (e.g. *_Data\Plugins\x86_64 or *_Data\Plugins)
$unityPluginDirs = @()
# For Unity games, discover dedicated Plugins folders (e.g. *_Data\Plugins\x86_64 or *_Data\Plugins)
$unityPluginDirs = @()
$unityDataDirs = Get-ChildItem -LiteralPath $TargetDir -Directory -Filter "*_Data" -Recurse -ErrorAction SilentlyContinue
foreach ($uData in $unityDataDirs) {
    $p64 = Join-Path $uData.FullName "Plugins\x86_64"
    $pRoot = Join-Path $uData.FullName "Plugins"
    if (Test-Path -LiteralPath $p64) { $unityPluginDirs += $p64 }
    elseif (Test-Path -LiteralPath $pRoot) { $unityPluginDirs += $pRoot }
}

$steamDlls = Get-ChildItem -LiteralPath $TargetDir -Filter "steam_api64.dll" -Recurse -ErrorAction SilentlyContinue
foreach ($dll in $steamDlls) {
    if ($pluginDirs -notcontains $dll.DirectoryName) { $pluginDirs += $dll.DirectoryName }
}

$valveDlls = Get-ChildItem -LiteralPath $TargetDir -Filter "steam_api64_valve.dll" -Recurse -ErrorAction SilentlyContinue
foreach ($v in $valveDlls) {
    if ($pluginDirs -notcontains $v.DirectoryName) { $pluginDirs += $v.DirectoryName }
}

# If Unity plugins directory was found, prioritize it and purge any stray proxy in the root folder ($ExeDir)
if ($unityPluginDirs.Count -gt 0) {
    foreach ($up in $unityPluginDirs) {
        if ($pluginDirs -notcontains $up) { $pluginDirs += $up }
    }
    if ($pluginDirs.Count -gt 1 -and ($pluginDirs -contains $ExeDir)) {
        $pluginDirs = @($pluginDirs | Where-Object { $_ -ne $ExeDir })
        $straySteam = Join-Path $ExeDir "steam_api64.dll"
        $strayValve = Join-Path $ExeDir "steam_api64_valve.dll"
        if (Test-Path -LiteralPath $straySteam) { Remove-Item -LiteralPath $straySteam -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $strayValve) { Remove-Item -LiteralPath $strayValve -Force -ErrorAction SilentlyContinue }
        Write-Host "  [OK] Cleaned stray steam_api64 DLLs from Unity root folder to prevent DLL shadowing" -ForegroundColor Green
    }
}

# For Unreal Engine, also ensure ExeDir is in pluginDirs so steam_api64.dll is deployed beside the shipping executable
if ($EngineType -eq "Unreal" -and ($pluginDirs -notcontains $ExeDir)) {
    $pluginDirs += $ExeDir
}

# -------------------------------------------------------
# x86 discovery: detect steam_api.dll (32-bit) locations
# Handles Unity IL2CPP x86 games (e.g. *_Data\Plugins\x86 or game root)
# -------------------------------------------------------
$pluginDirs32 = @()

# Unity x86 plugin subfolder: *_Data\Plugins\x86
foreach ($uData in $unityDataDirs) {
    $p32 = Join-Path $uData.FullName "Plugins\x86"
    if (Test-Path -LiteralPath $p32) {
        if ($pluginDirs32 -notcontains $p32) { $pluginDirs32 += $p32 }
    }
}

# Scan for standalone steam_api.dll (exactly "steam_api.dll", not steam_api64.dll)
$steamDlls32 = Get-ChildItem -LiteralPath $TargetDir -Filter "steam_api.dll" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "steam_api.dll" }
foreach ($dll32 in $steamDlls32) {
    if ($pluginDirs32 -notcontains $dll32.DirectoryName) { $pluginDirs32 += $dll32.DirectoryName }
}

# Also pick up dirs that only have steam_api_o.dll or steam_api_valve.dll (already-deployed x86)
$valveDlls32 = Get-ChildItem -LiteralPath $TargetDir -Filter "steam_api_valve.dll" -Recurse -ErrorAction SilentlyContinue
foreach ($v32 in $valveDlls32) {
    if ($pluginDirs32 -notcontains $v32.DirectoryName) { $pluginDirs32 += $v32.DirectoryName }
}

if ($pluginDirs32.Count -gt 0) {
    Write-Host "  [x86] Detected 32-bit steam_api.dll location(s): $($pluginDirs32 -join ', ')" -ForegroundColor Yellow
}

if ($pluginDirs.Count -eq 0) { $pluginDirs += $ExeDir }

switch ($OnlineMode) {
    "valve" {
        # --- Mode 1: ReFix Online por Steam (Valve Mode) ---
        # Uses ReFix proxy steam_api64.dll forwarding to steam_api64_valve.dll + AppID spoofing.
        # Enables direct .exe launching with full Steam Overlay injection.
        Write-Host "  [Valve Mode] Deploying ReFix proxy steam_api64.dll with Steam Overlay injection..." -ForegroundColor Cyan
        $proxyPath = Join-Path $BinDir "steam_api64.dll"
        $valveStockDll = Join-Path $BinDir "valve\steam_api64.dll"
        $goldbergDll = Join-Path $BinDir "goldberg\steam_api64.dll"
        if (-not (Test-Path -LiteralPath $proxyPath)) {
            Write-Host "  [ERROR] ReFix proxy steam_api64.dll not found at: $proxyPath" -ForegroundColor Red
            exit 1
        }
        foreach ($dir in $pluginDirs) {
            $valvePath = Join-Path $dir "steam_api64_valve.dll"
            $steamPath = Join-Path $dir "steam_api64.dll"
            $origPath  = Join-Path $dir "steam_api64_original.dll"

            # Check if existing $valvePath is actually Goldberg (e.g. from previous Goldberg deployment)
            $isGoldbergAtValve = $false
            if (Test-Path -LiteralPath $valvePath) {
                if (Test-Path -LiteralPath $goldbergDll) {
                    if ((Get-Item -LiteralPath $valvePath).Length -eq (Get-Item -LiteralPath $goldbergDll).Length) {
                        $isGoldbergAtValve = $true
                    }
                }
            }

            # If $origPath exists, make sure $valvePath is restored from $origPath
            if (Test-Path -LiteralPath $origPath) {
                if ((-not (Test-Path -LiteralPath $valvePath)) -or $isGoldbergAtValve) {
                    Copy-Item -LiteralPath $origPath -Destination $valvePath -Force
                    Write-Host "  [OK] Restored original Valve DLL from $origPath -> steam_api64_valve.dll in $dir" -ForegroundColor Green
                    $isGoldbergAtValve = $false
                }
            } elseif (Test-Path -LiteralPath $steamPath) {
                $isAlreadyProxy = ((Get-Item -LiteralPath $steamPath).Length -eq (Get-Item -LiteralPath $proxyPath).Length)
                $isGoldbergAtSteam = (Test-Path -LiteralPath $goldbergDll) -and ((Get-Item -LiteralPath $steamPath).Length -eq (Get-Item -LiteralPath $goldbergDll).Length)
                if ((-not $isAlreadyProxy) -and (-not $isGoldbergAtSteam)) {
                    # This is genuine original DLL
                    Copy-Item -LiteralPath $steamPath -Destination $origPath -Force
                    Copy-Item -LiteralPath $steamPath -Destination $valvePath -Force
                    Write-Host "  [OK] Preserved original steam_api64.dll -> steam_api64_original.dll & steam_api64_valve.dll in $dir" -ForegroundColor Green
                    $isGoldbergAtValve = $false
                }
            }

            # If valvePath is still missing or still Goldberg, use stock genuine Valve DLL as fallback
            if ((-not (Test-Path -LiteralPath $valvePath)) -or $isGoldbergAtValve) {
                if (Test-Path -LiteralPath $valveStockDll) {
                    Copy-Item -LiteralPath $valveStockDll -Destination $valvePath -Force
                    Write-Host "  [OK] Deployed stock genuine Valve steam_api64.dll -> steam_api64_valve.dll in $dir" -ForegroundColor Green
                } else {
                    Write-Host "  [WARNING] Genuine Valve steam_api64.dll not found; Steam Spacewar overlay/connection may fail!" -ForegroundColor Red
                }
            }

            try {
                Copy-Item -LiteralPath $proxyPath -Destination $steamPath -Force -ErrorAction Stop
                if (-not (Test-Path -LiteralPath $steamPath)) { throw "Target file not created: $steamPath" }
                Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
            } catch {
                Write-Host "  [ERROR] Failed to deploy steam_api64.dll to $dir : $_" -ForegroundColor Red
                exit 1
            }
        }

        # Also deploy x86 proxy if 32-bit game detected
        if ($pluginDirs32.Count -gt 0) {
            $proxyPath32 = Join-Path $BinDir "x86\steam_api.dll"
            if (Test-Path -LiteralPath $proxyPath32) {
                foreach ($dir32 in $pluginDirs32) {
                    $valvePath32 = Join-Path $dir32 "steam_api_o.dll"
                    $steamPath32 = Join-Path $dir32 "steam_api.dll"
                    $origPath32  = Join-Path $dir32 "steam_api_original.dll"

                    if (Test-Path -LiteralPath $origPath32) {
                        Copy-Item -LiteralPath $origPath32 -Destination $valvePath32 -Force
                        Write-Host "  [OK] Restored genuine Valve x86 steam_api.dll -> steam_api_o.dll in $dir32" -ForegroundColor Green
                    } elseif (Test-Path -LiteralPath $steamPath32) {
                        $isAlreadyProxy = ((Get-Item -LiteralPath $steamPath32).Length -eq (Get-Item -LiteralPath $proxyPath32).Length)
                        if ((-not (Test-Path -LiteralPath $valvePath32)) -and (-not $isAlreadyProxy)) {
                            Copy-Item -LiteralPath $steamPath32 -Destination $origPath32 -Force
                            Rename-Item -LiteralPath $steamPath32 -NewName "steam_api_o.dll" -Force
                            Write-Host "  [OK] Backed up original x86 steam_api.dll -> steam_api_o.dll in $dir32" -ForegroundColor Green
                        }
                    }
                    try {
                        Copy-Item -LiteralPath $proxyPath32 -Destination $steamPath32 -Force -ErrorAction Stop
                        if (-not (Test-Path -LiteralPath $steamPath32)) { throw "Target x86 file not created: $steamPath32" }
                        Write-Host "  [OK] Deployed ReFix proxy x86 steam_api.dll to $dir32" -ForegroundColor Green
                    } catch {
                        Write-Host "  [ERROR] Failed to deploy x86 steam_api.dll to $dir32 : $_" -ForegroundColor Red
                        exit 1
                    }
                }
            }
        }

        # Detect if game is 32-bit (x86) to prevent fatal 64-bit DLL injection into 32-bit processes
        $isX86Game = ($pluginDirs32.Count -gt 0)
        if (-not $isX86Game) {
            $gameExesInDir = Get-ChildItem -LiteralPath $ExeDir -Filter "*.exe" -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notlike "*UnityCrashHandler*" -and $_.Name -notlike "*crashpad*" }
            
            $candidateExes = @()
            $mainExe = $gameExesInDir | Where-Object { $_.Name -like "*$GameName*" } | Select-Object -First 1
            if ($mainExe) { $candidateExes += $mainExe }
            $candidateExes += ($gameExesInDir | Where-Object { $_.Name -notlike "*Launcher*" -and ($mainExe -eq $null -or $_.FullName -ne $mainExe.FullName) })
            $candidateExes += ($gameExesInDir | Where-Object { $_.Name -like "*Launcher*" })

            foreach ($ge in $candidateExes) {
                try {
                    $peBytes = [System.IO.File]::ReadAllBytes($ge.FullName)
                    if ($peBytes.Length -ge 0x40) {
                        $peOffset = [System.BitConverter]::ToInt32($peBytes, 0x3C)
                        if ($peOffset + 6 -le $peBytes.Length) {
                            $mach = [System.BitConverter]::ToUInt16($peBytes, $peOffset + 4)
                            if ($mach -eq 0x014c) { $isX86Game = $true; break }
                            elseif ($mach -eq 0x8664) { $isX86Game = $false; break }
                        }
                    }
                } catch {}
            }
        }

        # Also deploy winmm.dll to ExeDir for early Steam Overlay injection (64-bit only)
        $winmmPath = Join-Path $BinDir "winmm.dll"
        if ((Test-Path -LiteralPath $winmmPath) -and (-not $isX86Game)) {
            $targetWinmm = Join-Path $ExeDir "winmm.dll"
            $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
            try {
                if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item -LiteralPath $targetWinmm).Length -ne (Get-Item -LiteralPath $winmmPath).Length)) {
                    Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
                }
                Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force -ErrorAction Stop
                if (-not (Test-Path -LiteralPath $targetWinmm)) { throw "Target winmm.dll not created: $targetWinmm" }
                Write-Host "  [OK] Deployed ReFix winmm.dll proxy to root folder $ExeDir for early Steam Overlay injection" -ForegroundColor Green
            } catch {
                Write-Host "  [ERROR] Failed to deploy winmm.dll to $ExeDir : $_" -ForegroundColor Red
                exit 1
            }
        } elseif ($isX86Game) {
            # Critical: Ensure no 64-bit winmm.dll remains in 32-bit game directory
            $strayWinmm = Join-Path $ExeDir "winmm.dll"
            if (Test-Path -LiteralPath $strayWinmm) {
                try {
                    $wBytes = [System.IO.File]::ReadAllBytes($strayWinmm)
                    if ($wBytes.Length -ge 0x40) {
                        $peOff = [System.BitConverter]::ToInt32($wBytes, 0x3C)
                        $mach = [System.BitConverter]::ToUInt16($wBytes, $peOff + 4)
                        if ($mach -eq 0x8664) {
                            Remove-Item -LiteralPath $strayWinmm -Force -ErrorAction SilentlyContinue
                            Write-Host "  [CRITICAL FIX] Removed stray 64-bit winmm.dll from 32-bit game directory: $strayWinmm" -ForegroundColor Yellow
                        }
                    }
                } catch {}
            }
        }

        # Synchronize ReFix.ini in ExeDir
        $reFixIniPath = Join-Path $ExeDir "ReFix.ini"
        $identity = Get-Or-Generate-Identity -ConfigPath $reFixIniPath -InputName $UserName -BasePath $TargetDir
        $finalUserName = if ($UserName) { $UserName } else { $identity.Name }
        $finalSteamId = $identity.SteamId
        $filterVal = if ($RealAppId -and $RealAppId -ne "0") { $RealAppId } else { "480" }
        $maskVal = if ($MaskAppId) { $MaskAppId } else { "480" }
        $p2pPortVal = if ($unaeTopology -and $unaeTopology.P2PPort) { $unaeTopology.P2PPort } else { "7777" }

        Generate-UNAE-ReFixIni -IniPath $reFixIniPath -P @{
            GameName            = $GameName
            EngineType          = $EngineType
            OnlineMode          = "valve"
            MaskAppId           = $maskVal
            RealAppId           = $filterVal
            Language            = $Language
            DLCMode             = $DLCMode
            DLCs                = $DLCs
            UserName            = $finalUserName
            SteamId             = $finalSteamId
            AutoGenerateSteamId = $identity.AutoGenerateSteamId
            ListenPort          = $ListenPort
            CustomBroadcasts    = $CustomBroadcasts
            AppIdRealtime       = $AppIdRealtime
            PhotonAppId         = $PhotonAppId
            AppIdFusion         = $AppIdFusion
            AppIdVoice          = $AppIdVoice
            ArbitrationMode     = $ArbitrationMode
            DefaultRegion       = $DefaultRegion
            PhotonRegion        = $PhotonRegion
            P2PPort             = $p2pPortVal
        }
        Write-Host "  [OK] Configured unified ReFix.ini in $ExeDir" -ForegroundColor Green
    }

    "photon" {
        # --- Photon Mode (Method 2) ---
        # Deploys ReFix proxy steam_api64.dll and BepInEx + NekogiriFix
        Write-Host "  [Photon Mode] Deploying ReFix proxy steam_api64.dll..." -ForegroundColor Cyan
        $proxyPath = Join-Path $BinDir "steam_api64.dll"
        if (-not (Test-Path $proxyPath)) {
            Write-Host "  [ERROR] ReFix proxy steam_api64.dll not found at: $proxyPath" -ForegroundColor Red
            exit 1
        }
        foreach ($dir in $pluginDirs) {
            $valvePath = Join-Path $dir "steam_api64_valve.dll"
            $steamPath = Join-Path $dir "steam_api64.dll"
            if (Test-Path $steamPath) {
                $isAlreadyProxy = $false
                if (Test-Path $proxyPath) {
                    if ((Get-Item $steamPath).Length -eq (Get-Item $proxyPath).Length) {
                        $isAlreadyProxy = $true
                    }
                }
                if (-not (Test-Path $valvePath)) {
                    if (-not $isAlreadyProxy) {
                        Rename-Item -Path $steamPath -NewName "steam_api64_valve.dll" -Force
                        Write-Host "  [OK] Backed up original steam_api64.dll -> steam_api64_valve.dll in $dir" -ForegroundColor Green
                    } else {
                        Write-Host "  [NOTICE] Existing steam_api64.dll in $dir is already ReFix proxy (skipping backup to preserve original)" -ForegroundColor Yellow
                    }
                }
            }
            Copy-Item -Path $proxyPath -Destination $dir -Force
            Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
        }

        # Synchronize unified ReFix.ini in ExeDir for UNAE and DRPI
        $reFixIniPath = Join-Path $ExeDir "ReFix.ini"
        $identity = Get-Or-Generate-Identity -ConfigPath $reFixIniPath -InputName $UserName -BasePath $TargetDir
        $finalUserName = if ($UserName) { $UserName } else { $identity.Name }
        $finalSteamId = $identity.SteamId
        $filterVal = if ($RealAppId -and $RealAppId -ne "0") { $RealAppId } else { "480" }
        $maskVal = if ($MaskAppId) { $MaskAppId } else { "480" }
        $p2pPortVal = if ($unaeTopology -and $unaeTopology.P2PPort) { $unaeTopology.P2PPort } else { "7777" }

        Generate-UNAE-ReFixIni -IniPath $reFixIniPath -P @{
            GameName            = $GameName
            EngineType          = $EngineType
            OnlineMode          = "photon"
            MaskAppId           = $maskVal
            RealAppId           = $filterVal
            Language            = $Language
            DLCMode             = $DLCMode
            DLCs                = $DLCs
            UserName            = $finalUserName
            SteamId             = $finalSteamId
            AutoGenerateSteamId = $identity.AutoGenerateSteamId
            ListenPort          = $ListenPort
            CustomBroadcasts    = $CustomBroadcasts
            AppIdRealtime       = $AppIdRealtime
            PhotonAppId         = $PhotonAppId
            AppIdFusion         = $AppIdFusion
            AppIdVoice          = $AppIdVoice
            ArbitrationMode     = $ArbitrationMode
            DefaultRegion       = $DefaultRegion
            PhotonRegion        = $PhotonRegion
            P2PPort             = $p2pPortVal
        }
        Write-Host "  [OK] Configured unified ReFix.ini in $ExeDir" -ForegroundColor Green
    }

    { $_ -in @("goldberg", "offline", "lan") } {
        # --- Mode 2: Re:Goldberg LAN sin Steam ---
        # Deploys Goldberg Steam Emulator backend with complete local settings synchronization,
        # persistent identity, LAN matchmaking discovery, and portable saves.
        Write-Host "  [Re:Goldberg LAN Mode] Deploying Goldberg Steam Emulator backend..." -ForegroundColor Cyan
        $goldbergDll = Join-Path $BinDir "goldberg\steam_api64.dll"
        if (-not (Test-Path $goldbergDll)) {
            Write-Host "  [ERROR] Goldberg steam_api64.dll not found at: $goldbergDll" -ForegroundColor Red
            exit 1
        }

        # Step 2a: Generate steam_interfaces.txt from original DLL if interface generator is available
        $genTool = Join-Path $BinDir "goldberg\tools\generate_interfaces_file.exe"
        $generatedInterfacesFile = $null

        foreach ($dir in $pluginDirs) {
            $valvePath = Join-Path $dir "steam_api64_valve.dll"
            $steamPath = Join-Path $dir "steam_api64.dll"

            # Check if original DLL exists to extract interfaces before overwriting
            $targetForInterfaces = $null
            if (Test-Path $valvePath) {
                $targetForInterfaces = $valvePath
            } elseif (Test-Path $steamPath) {
                $targetForInterfaces = $steamPath
            }

            if ($targetForInterfaces -and (Test-Path $genTool)) {
                try {
                    $origWorkingDir = Get-Location
                    Set-Location $dir
                    & $genTool $targetForInterfaces | Out-Null
                    Set-Location $origWorkingDir

                    $localGenFile = Join-Path $dir "steam_interfaces.txt"
                    if (Test-Path -LiteralPath $localGenFile) {
                        # Clean and deduplicate keeping the highest interface version for each prefix
                        $rawLines = Get-Content -LiteralPath $localGenFile | Where-Object { $_.Trim() -ne "" }
                        $families = [ordered]@{}
                        foreach ($line in $rawLines) {
                            $trimmed = $line.Trim()
                            if ($trimmed -match "^([A-Za-z_]+?)(\d+)$") {
                                $prefix = $Matches[1]
                                $ver = [int]$Matches[2]
                                if (-not $families.Contains($prefix) -or $families[$prefix].ver -lt $ver) {
                                    $families[$prefix] = @{ line = $trimmed; ver = $ver }
                                }
                            } else {
                                $families[$trimmed] = @{ line = $trimmed; ver = 0 }
                            }
                        }
                        $cleanLines = @($families.Values | ForEach-Object { $_.line })
                        if ($cleanLines.Count -gt 0) {
                            [System.IO.File]::WriteAllLines($localGenFile, [string[]]$cleanLines)
                            $generatedInterfacesFile = $localGenFile
                            Write-Host "  [OK] Generated & optimized steam_interfaces.txt from original Steam API DLL" -ForegroundColor Green
                        }
                    }
                } catch {
                    Write-Host "  [NOTICE] Interface generator notice: $_" -ForegroundColor Yellow
                }
            }

            $proxyPath = Join-Path $BinDir "steam_api64.dll"
            $origPath  = Join-Path $dir "steam_api64_original.dll"

            # Preserve genuine Valve DLL before placing Goldberg as steam_api64_valve.dll
            if (Test-Path -LiteralPath $valvePath) {
                if ((Get-Item -LiteralPath $valvePath).Length -ne (Get-Item -LiteralPath $goldbergDll).Length) {
                    if (-not (Test-Path -LiteralPath $origPath)) {
                        Copy-Item -LiteralPath $valvePath -Destination $origPath -Force
                        Write-Host "  [OK] Preserved genuine Valve DLL -> steam_api64_original.dll in $dir" -ForegroundColor Green
                    }
                }
            } elseif (Test-Path -LiteralPath $steamPath) {
                $isAlreadyProxy = (Test-Path -LiteralPath $proxyPath) -and ((Get-Item -LiteralPath $steamPath).Length -eq (Get-Item -LiteralPath $proxyPath).Length)
                $isAlreadyGoldberg = ((Get-Item -LiteralPath $steamPath).Length -eq (Get-Item -LiteralPath $goldbergDll).Length)
                if ((-not $isAlreadyProxy) -and (-not $isAlreadyGoldberg)) {
                    if (-not (Test-Path -LiteralPath $origPath)) {
                        Copy-Item -LiteralPath $steamPath -Destination $origPath -Force
                        Write-Host "  [OK] Preserved genuine Valve DLL -> steam_api64_original.dll in $dir" -ForegroundColor Green
                    }
                }
            }

            try {
                # Place Goldberg DLL as steam_api64_valve.dll
                Copy-Item -LiteralPath $goldbergDll -Destination $valvePath -Force -ErrorAction Stop
                if (-not (Test-Path -LiteralPath $valvePath)) { throw "Target file not created: $valvePath" }
                Write-Host "  [OK] Deployed Goldberg emulator backend as steam_api64_valve.dll to $dir" -ForegroundColor Green

                # Deploy ReFix proxy as steam_api64.dll (provides SteamInternal_SteamAPI_Init and all API exports)
                Copy-Item -LiteralPath $proxyPath -Destination $steamPath -Force -ErrorAction Stop
                if (-not (Test-Path -LiteralPath $steamPath)) { throw "Target file not created: $steamPath" }
                Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
            } catch {
                Write-Host "  [ERROR] Failed to deploy Goldberg backend / proxy to $dir : $_" -ForegroundColor Red
                exit 1
            }
        }

        # --- x86 deployment: Re:Goldberg for 32-bit games (Unity IL2CPP x86, etc.) ---
        # Pattern: goldberg x86 -> steam_api_o.dll (backend loaded by proxy32)
        #          ReFix proxy32 -> steam_api.dll (intercepts Steamworks.NET)
        if ($pluginDirs32.Count -gt 0) {
            $goldbergDll32 = Join-Path $BinDir "goldberg\steam_api.dll"
            $proxyDll32    = Join-Path $BinDir "x86\steam_api.dll"

            if (-not (Test-Path -LiteralPath $goldbergDll32)) {
                Write-Host "  [NOTICE] Goldberg x86 backend not found at $goldbergDll32 - skipping x86 deployment" -ForegroundColor Yellow
            } elseif (-not (Test-Path -LiteralPath $proxyDll32)) {
                Write-Host "  [NOTICE] ReFix proxy x86 not found at $proxyDll32 - skipping x86 deployment (run build_x86.bat first)" -ForegroundColor Yellow
            } else {
                Write-Host "  [Re:Goldberg x86] Deploying 32-bit backend + proxy to $($pluginDirs32.Count) location(s)..." -ForegroundColor Cyan
                foreach ($dir32 in $pluginDirs32) {
                    $steamPath32   = Join-Path $dir32 "steam_api.dll"
                    $backendPath32 = Join-Path $dir32 "steam_api_o.dll"     # Goldberg x86 backend
                    $origPath32    = Join-Path $dir32 "steam_api_original.dll"  # Original Steam (preserved)

                    # Backup original steam_api.dll if it is not our proxy32 (compare sizes)
                    if (Test-Path -LiteralPath $steamPath32) {
                        $existingSz = (Get-Item -LiteralPath $steamPath32).Length
                        $proxySz    = (Get-Item -LiteralPath $proxyDll32).Length
                        if ($existingSz -ne $proxySz) {
                            # Not our proxy - could be original Steam or goldberg; preserve as _original
                            if (-not (Test-Path -LiteralPath $origPath32)) {
                                Copy-Item -LiteralPath $steamPath32 -Destination $origPath32 -Force
                                Write-Host "  [OK] Backed up original x86 steam_api.dll -> steam_api_original.dll in $dir32" -ForegroundColor Green
                            }
                        }
                    }

                    try {
                        # Deploy Goldberg x86 as backend (steam_api_o.dll loaded by proxy32's EnsureOrigLoaded)
                        Copy-Item -LiteralPath $goldbergDll32 -Destination $backendPath32 -Force -ErrorAction Stop
                        Write-Host "  [OK] Deployed Goldberg x86 emulator as steam_api_o.dll to $dir32" -ForegroundColor Green

                        # Deploy ReFix proxy32 as steam_api.dll
                        Copy-Item -LiteralPath $proxyDll32 -Destination $steamPath32 -Force -ErrorAction Stop
                        Write-Host "  [OK] Deployed ReFix proxy x86 steam_api.dll to $dir32" -ForegroundColor Green
                    } catch {
                        Write-Host "  [ERROR] Failed to deploy x86 Goldberg emulator / proxy to $dir32 : $_" -ForegroundColor Red
                        exit 1
                    }

                    # Generate steam_interfaces.txt for this x86 dir if tool is available
                    if (Test-Path -LiteralPath $genTool) {
                        try {
                            $origWd32 = Get-Location
                            Set-Location $dir32
                            & $genTool $backendPath32 | Out-Null
                            Set-Location $origWd32
                            $genFile32 = Join-Path $dir32 "steam_interfaces.txt"
                            if (Test-Path $genFile32) {
                                Write-Host "  [OK] Generated steam_interfaces.txt for x86 dir $dir32" -ForegroundColor Green
                            }
                        } catch {
                            Write-Host "  [NOTICE] x86 interface generator: $_" -ForegroundColor Yellow
                        }
                    }
                }
            }
        }
    }
}

# ============================================================
# Step 3: Engine-specific patches + Mode-specific deployment
# ============================================================
if ($OnlineMode -in @("goldberg", "offline", "lan")) {
    # ------------------------------------------------------------
    # Mode 2: Re:Goldberg LAN sin Steam - Full Configuration Sync
    # ------------------------------------------------------------
    Write-Host "[3/6] Synchronizing Re:Goldberg LAN Configuration & Persistent Identity..." -ForegroundColor Cyan

    $reFixIniPath = Join-Path $ExeDir "ReFix.ini"
    $identity = Get-Or-Generate-Identity -ConfigPath $reFixIniPath -InputName $UserName -BasePath $TargetDir

    $finalRealAppId = $RealAppId
    if (-not $finalRealAppId -or $finalRealAppId -eq "0") {
        if (Test-Path $reFixIniPath) {
            $iniContent = Get-Content $reFixIniPath -ErrorAction SilentlyContinue
            foreach ($line in $iniContent) {
                if ($line -match "^\s*RealAppId\s*=\s*([0-9]+)") { $finalRealAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $finalRealAppId -or $finalRealAppId -eq "0") { $finalRealAppId = "480" }

    $finalLanguage = $Language
    if (-not $finalLanguage) { $finalLanguage = "english" }

    $finalListenPort = $ListenPort
    if (-not $finalListenPort -or $finalListenPort -eq "47584") {
        if (Test-Path $reFixIniPath) {
            $iniContent = Get-Content $reFixIniPath -ErrorAction SilentlyContinue
            foreach ($line in $iniContent) {
                if ($line -match "^\s*DiscoveryPort\s*=\s*([0-9]+)") { $finalListenPort = $Matches[1].Trim() }
                elseif ($line -match "^\s*ListenPort\s*=\s*([0-9]+)") { $finalListenPort = $Matches[1].Trim() }
            }
        }
    }
    if (-not $finalListenPort) { $finalListenPort = "47584" }

    # Create local saves folder in ExeDir for portable storage
    $savesDir = Join-Path $ExeDir "saves"
    if (-not (Test-Path -LiteralPath $savesDir)) {
        [System.IO.Directory]::CreateDirectory($savesDir) | Out-Null
        Write-Host "  [OK] Initialized portable save directory: $savesDir" -ForegroundColor Green
    }

    # Populate steam_settings in ExeDir and all plugin directories (x64 + x86)
    $settingsDirsToPopulate = @($ExeDir)
    foreach ($pDir in $pluginDirs) {
        if ($settingsDirsToPopulate -notcontains $pDir) { $settingsDirsToPopulate += $pDir }
    }
    # Also populate x86 dirs (e.g. HushHush_Data\Plugins\x86) so goldberg x86 backend finds its config
    foreach ($pDir32 in $pluginDirs32) {
        if ($settingsDirsToPopulate -notcontains $pDir32) { $settingsDirsToPopulate += $pDir32 }
    }

    foreach ($baseDir in $settingsDirsToPopulate) {
        $settingsDir = Join-Path $baseDir "steam_settings"
        if (-not (Test-Path -LiteralPath $settingsDir)) {
            [System.IO.Directory]::CreateDirectory($settingsDir) | Out-Null
        }

        # 1. Base legacy text configuration files (guaranteed support across all Goldberg versions)
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "steam_appid.txt"), "$finalRealAppId`r`n")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "force_account_name.txt"), "$($identity.Name)")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "force_steamid.txt"), "$($identity.SteamId)")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "user_steam_id.txt"), "$($identity.SteamId)")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "force_language.txt"), "$finalLanguage")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "force_listen_port.txt"), "$finalListenPort")
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "local_save.txt"), "saves")

        # 2. Advanced INI configuration files (for modern gbe_fork builds)
        $configsUser = @"
; =============================================================================
; Goldberg Emulator - User Configuration (Auto-synchronized from ReFix.ini)
; =============================================================================
[user::general]
account_name=$($identity.Name)
account_steamid=$($identity.SteamId)
language=$finalLanguage

[user::saves]
local_save_path=saves
"@
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "configs.user.ini"), $configsUser)

        $configsMain = @"
; =============================================================================
; Goldberg Emulator - Main Configuration (Auto-synchronized from ReFix.ini)
; =============================================================================
[main::connectivity]
listen_port=$finalListenPort
offline=false
disable_networking=false
disable_lan_only=false
"@
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "configs.main.ini"), $configsMain)

        # Determine effective DLC configuration
        $effectiveDlcMode = if ($DLCMode) { $DLCMode.ToLower() } else { "all" }
        if ($DLCs -eq "none") { $effectiveDlcMode = "none" }
        elseif ($DLCs -eq "all") { $effectiveDlcMode = "all" }
        elseif ($DLCs -and $DLCs -ne "all" -and $DLCs -ne "none") { $effectiveDlcMode = "custom" }

        $unlockAllDlcStr = if ($effectiveDlcMode -eq "none") { "false" } elseif ($effectiveDlcMode -eq "custom") { "false" } else { "true" }
        $bypassLicenseVal = if ($effectiveDlcMode -eq "none") { "false" } else { "true" }

        $configsApp = @"
; =============================================================================
; Goldberg Emulator - App Configuration (Auto-synchronized from ReFix.ini)
; =============================================================================
[app::general]
appid=$finalRealAppId
unlock_all_dlc=$unlockAllDlcStr
"@
        [System.IO.File]::WriteAllText((Join-Path $settingsDir "configs.app.ini"), $configsApp)

        # 3. Optional DLC and Broadcast configuration
        $dlcTxtPath = Join-Path $settingsDir "DLC.txt"
        if ($effectiveDlcMode -eq "none") {
            # Write empty DLC.txt so Goldberg explicitly disables/locks all DLCs
            [System.IO.File]::WriteAllText($dlcTxtPath, "")
        } elseif ($effectiveDlcMode -eq "custom" -or ($DLCs -and $DLCs -ne "all")) {
            $dlcLines = @()
            $dlcItems = $DLCs -split '[,;]'
            foreach ($item in $dlcItems) {
                $trimItem = $item.Trim()
                if ($trimItem) {
                    if ($trimItem -match "=") { $dlcLines += $trimItem }
                    else { $dlcLines += "$trimItem=DLC $trimItem" }
                }
            }
            [System.IO.File]::WriteAllText($dlcTxtPath, ($dlcLines -join "`r`n") + "`r`n")
        } elseif ($DLCs -and $DLCs -ne "all") {
            $dlcLines = @()
            $dlcItems = $DLCs -split '[,;]'
            foreach ($item in $dlcItems) {
                $trimItem = $item.Trim()
                if ($trimItem) {
                    if ($trimItem -match "=") { $dlcLines += $trimItem }
                    else { $dlcLines += "$trimItem=DLC $trimItem" }
                }
            }
            if ($dlcLines.Count -gt 0) {
                [System.IO.File]::WriteAllText($dlcTxtPath, ($dlcLines -join "`r`n") + "`r`n")
            }
        }

        if ($CustomBroadcasts) {
            $bcastLines = ($CustomBroadcasts -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            if ($bcastLines.Count -gt 0) {
                [System.IO.File]::WriteAllText((Join-Path $settingsDir "custom_broadcasts.txt"), ($bcastLines -join "`r`n") + "`r`n")
            }
        }

        # 4. Copy generated interfaces file into steam_settings if present
        if ($generatedInterfacesFile -and (Test-Path $generatedInterfacesFile)) {
            $destItf = Join-Path $settingsDir "steam_interfaces.txt"
            if ($generatedInterfacesFile -ne $destItf) {
                Copy-Item -Path $generatedInterfacesFile -Destination $destItf -Force
            }
        }

        # 5. Place steam_appid.txt and local_save.txt in baseDir
        [System.IO.File]::WriteAllText((Join-Path $baseDir "steam_appid.txt"), "$finalRealAppId`r`n")
        [System.IO.File]::WriteAllText((Join-Path $baseDir "local_save.txt"), "saves`r`n")

        Write-Host "  [OK] Synchronized steam_settings/ in $baseDir (User: $($identity.Name), SteamID: $($identity.SteamId), Port: $finalListenPort, DLC Mode: $effectiveDlcMode)" -ForegroundColor Green
    }

    # Synchronize and write unified ReFix.ini in ExeDir
    $p2pPortVal = if ($unaeTopology -and $unaeTopology.P2PPort) { $unaeTopology.P2PPort } else { "7777" }
    Generate-UNAE-ReFixIni -IniPath $reFixIniPath -P @{
        GameName            = $GameName
        EngineType          = $EngineType
        OnlineMode          = "goldberg"
        MaskAppId           = $MaskAppId
        RealAppId           = $finalRealAppId
        Language            = $finalLanguage
        DLCMode             = $effectiveDlcMode
        DLCs                = $DLCs
        UserName            = $identity.Name
        SteamId             = $identity.SteamId
        AutoGenerateSteamId = $identity.AutoGenerateSteamId
        ListenPort          = $finalListenPort
        CustomBroadcasts    = $CustomBroadcasts
        AppIdRealtime       = $AppIdRealtime
        PhotonAppId         = $PhotonAppId
        AppIdFusion         = $AppIdFusion
        AppIdVoice          = $AppIdVoice
        ArbitrationMode     = $ArbitrationMode
        DefaultRegion       = $DefaultRegion
        PhotonRegion        = $PhotonRegion
        P2PPort             = $p2pPortVal
    }
    Write-Host "  [OK] Written unified ReFix.ini in $ExeDir" -ForegroundColor Green

    # Generate portable 1-click LAN Firewall helper for USB / other PCs
    $fwHelperBat = Join-Path $ExeDir "Configure_LAN_Firewall.bat"
    $fwBatLines = @(
        '@echo off',
        'setlocal enabledelayedexpansion',
        'title ReFix - LAN Firewall Configuration Tool',
        '',
        'set "SCRIPT_DIR=%~dp0"',
        'if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"',
        '',
        'echo ====================================================================',
        'echo             ReFix - LAN Firewall Configuration Tool',
        'echo ====================================================================',
        'echo:',
        'echo This script authorizes the game and UDP LAN port in Windows Firewall',
        'echo to enable local multiplayer across LAN / Flash Drives without warnings.',
        'echo:',
        '',
        'net session >nul 2>&1',
        'if !errorlevel! neq 0 (',
        '    echo [INFO] Requesting Administrator permissions...',
        '    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList ''/c \"\"%~f0\"\" --elevated'' -Verb RunAs"',
        '    exit /b',
        ')',
        '',
        'set "GAME_EXE="',
        'for %%F in ("!SCRIPT_DIR!\*.exe") do (',
        '    if /i not "%%~nxF"=="UnityCrashHandler64.exe" (',
        '        if /i not "%%~nxF"=="crashpad_handler.exe" (',
        '            if /i not "%%~nxF"=="F10.exe" (',
        '                if "!GAME_EXE!"=="" set "GAME_EXE=%%~dpnxF"',
        '            )',
        '        )',
        '    )',
        ')',
        '',
        "set `"LAN_PORT=$finalListenPort`"",
        'if exist "!SCRIPT_DIR!\ReFix.ini" (',
        '    for /f "tokens=1,* delims==" %%A in (''type "!SCRIPT_DIR!\ReFix.ini"'') do (',
        '        if /i "%%A"=="DiscoveryPort" set "LAN_PORT=%%B"',
        '        if /i "%%A"=="ListenPort" set "LAN_PORT=%%B"',
        '    )',
        ')',
        '',
        'echo Configuring Windows Firewall rules...',
        'if not "!GAME_EXE!"=="" (',
        '    for %%I in ("!GAME_EXE!") do set "EXE_NAME=%%~nxI"',
        '    netsh advfirewall firewall delete rule name="ReFix - !EXE_NAME! (TCP In)" >nul 2>&1',
        '    netsh advfirewall firewall delete rule name="ReFix - !EXE_NAME! (UDP In)" >nul 2>&1',
        '    netsh advfirewall firewall delete rule name="ReFix - !EXE_NAME! (TCP Out)" >nul 2>&1',
        '    netsh advfirewall firewall delete rule name="ReFix - !EXE_NAME! (UDP Out)" >nul 2>&1',
        '    netsh advfirewall firewall add rule name="ReFix - !EXE_NAME! (TCP In)" dir=in action=allow program="!GAME_EXE!" protocol=TCP enable=yes profile=any >nul 2>&1',
        '    netsh advfirewall firewall add rule name="ReFix - !EXE_NAME! (UDP In)" dir=in action=allow program="!GAME_EXE!" protocol=UDP enable=yes profile=any >nul 2>&1',
        '    netsh advfirewall firewall add rule name="ReFix - !EXE_NAME! (TCP Out)" dir=out action=allow program="!GAME_EXE!" protocol=TCP enable=yes profile=any >nul 2>&1',
        '    netsh advfirewall firewall add rule name="ReFix - !EXE_NAME! (UDP Out)" dir=out action=allow program="!GAME_EXE!" protocol=UDP enable=yes profile=any >nul 2>&1',
        '    echo [OK] Rules configured for executable: !EXE_NAME!',
        ')',
        '',
        'netsh advfirewall firewall delete rule name="ReFix - Goldberg LAN Discovery (UDP In)" >nul 2>&1',
        'netsh advfirewall firewall delete rule name="ReFix - Goldberg LAN Discovery (UDP Out)" >nul 2>&1',
        'netsh advfirewall firewall add rule name="ReFix - Goldberg LAN Discovery (UDP In)" dir=in action=allow protocol=UDP localport=!LAN_PORT! enable=yes profile=any >nul 2>&1',
        'netsh advfirewall firewall add rule name="ReFix - Goldberg LAN Discovery (UDP Out)" dir=out action=allow protocol=UDP remoteport=!LAN_PORT! enable=yes profile=any >nul 2>&1',
        'echo [OK] UDP Port !LAN_PORT! authorized for LAN discovery.',
        '',
        'echo:',
        'echo ====================================================================',
        'echo  Firewall configured successfully. Ready for LAN multiplayer!',
        'echo ====================================================================',
        'echo:',
        'echo Press any key to exit...',
        'pause >nul',
        'exit /b 0'
    )
    [System.IO.File]::WriteAllText($fwHelperBat, ($fwBatLines -join "`r`n") + "`r`n")
    Write-Host "  [OK] Generated portable LAN Firewall helper: $fwHelperBat" -ForegroundColor Green

    # Audit log
    $auditLogPath = Join-Path $ExeDir "ReFix.log"
    $logEntry = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] [ReFix AutoDeploy] Deployed Mode: Re:Goldberg LAN without Steam | Game: $GameName | AppID: $finalRealAppId | SteamID: $($identity.SteamId) | User: $($identity.Name) | Port: $finalListenPort | LocalSave: saves`r`n"
    [System.IO.File]::AppendAllText($auditLogPath, $logEntry)

}

# ============================================================
# Step 4: Engine-specific deployment & patches (All Modes)
# ============================================================
if ($EngineType -eq "Unity") {
    Write-Host "[4/6] Processing Unity deployment (mode: $OnlineMode)..." -ForegroundColor Cyan

    $reFixIni = Join-Path $ExeDir "ReFix.ini"
    $targetAppId = "480"
    if (Test-Path $reFixIni) {
        $iniLines = Get-Content $reFixIni -ErrorAction SilentlyContinue
        foreach ($line in $iniLines) {
            if ($OnlineMode -eq "valve") {
                if ($line -match "^\s*MaskAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^\s*RealAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
                if ($targetAppId -eq "0" -or -not $targetAppId) {
                    if ($line -match "^\s*MaskAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
                }
            }
        }
    }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }

    # Place steam_appid.txt in game root and all plugin directories
    $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
    [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
    Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $ExeDir" -ForegroundColor Green

    foreach ($dir in $pluginDirs) {
        if ($dir -ne $ExeDir) {
            $dirAppIdPath = Join-Path $dir "steam_appid.txt"
            [System.IO.File]::WriteAllText($dirAppIdPath, "$targetAppId`r`n")
            Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $dir" -ForegroundColor Green
        }
    }

    # ------------------------------------------------------------
    # Photon Fusion / nanosockets Detection & Asset Configuration
    # ------------------------------------------------------------
    $nanosocketsDll = Get-ChildItem -Path $TargetDir -Filter "nanosockets.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nanosocketsDll) {
        Write-Host "  [DETECT] Detected Photon Fusion nanosockets transport: $($nanosocketsDll.FullName)" -ForegroundColor Cyan
        
        # Check for level0 or serialized assets containing Photon AppID
        $level0Files = Get-ChildItem -Path $TargetDir -Filter "level0" -Recurse -File -ErrorAction SilentlyContinue
        foreach ($lvl in $level0Files) {
            try {
                $lvlBytes = [System.IO.File]::ReadAllBytes($lvl.FullName)
                $defaultFusionGuid = "1d51fe8a-4821-4958-86c2-896b93277d81"
                $defaultBytes = [System.Text.Encoding]::ASCII.GetBytes($defaultFusionGuid)
                
                # Check known offset 16052 first or scan full file
                $foundIdx = -1
                if ($lvlBytes.Length -ge (16052 + 36)) {
                    $matched16052 = $true
                    for ($j = 0; $j -lt 36; $j++) {
                        if ($lvlBytes[16052 + $j] -ne $defaultBytes[$j]) {
                            $matched16052 = $false
                            break
                        }
                    }
                    if ($matched16052) {
                        $foundIdx = 16052
                    } else {
                        # Check if offset 16052 contains a 36-char GUID string
                        $strAt16052 = [System.Text.Encoding]::ASCII.GetString($lvlBytes, 16052, 36)
                        if ($strAt16052 -match "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") {
                            $foundIdx = 16052
                        }
                    }
                }

                # If not found at 16052, perform full file scan for default PlaySide GUID
                if ($foundIdx -lt 0) {
                    for ($i = 0; $i -le ($lvlBytes.Length - 36); $i++) {
                        $matched = $true
                        for ($j = 0; $j -lt 36; $j++) {
                            if ($lvlBytes[$i + $j] -ne $defaultBytes[$j]) {
                                $matched = $false
                                break
                            }
                        }
                        if ($matched) {
                            $foundIdx = $i
                            break
                        }
                    }
                }
                
                if ($foundIdx -ge 0) {
                    Write-Host "  [DETECT] Located Photon Fusion AppID in $($lvl.Name) at offset $foundIdx" -ForegroundColor Yellow
                    
                    # Determine target AppID to patch (if provided)
                    $patchAppId = $AppIdFusion
                    if (-not $patchAppId) { $patchAppId = $PhotonAppId }
                    if (-not $patchAppId -and (Test-Path $reFixIni)) {
                        $iniContent = Get-Content $reFixIni -ErrorAction SilentlyContinue
                        foreach ($l in $iniContent) {
                            if ($l -match "^\s*AppIdFusion\s*=\s*([a-f0-9\-]{36})") { $patchAppId = $Matches[1].Trim() }
                            if ($l -match "^\s*PhotonFusionAppId\s*=\s*([a-f0-9\-]{36})") { $patchAppId = $Matches[1].Trim() }
                            if ($l -match "^\s*PhotonAppId\s*=\s*([a-f0-9\-]{36})") { $patchAppId = $Matches[1].Trim() }
                        }
                    }
                    
                    $patchVoiceId = $AppIdVoice
                    if (-not $patchVoiceId -and (Test-Path $targetIni)) {
                        $iniContent = Get-Content $targetIni
                        foreach ($l in $iniContent) {
                            if ($l -match "^\s*AppIdVoice\s*=\s*([a-f0-9\-]{36})") { $patchVoiceId = $Matches[1].Trim() }
                            if ($l -match "^\s*PhotonVoiceAppId\s*=\s*([a-f0-9\-]{36})") { $patchVoiceId = $Matches[1].Trim() }
                        }
                    }

                    if ($patchAppId -and $patchAppId.Length -eq 36 -and $patchAppId -ne $defaultFusionGuid) {
                        $backupLvl = "$($lvl.FullName).original"
                        if (-not (Test-Path $backupLvl)) {
                            Copy-Item -Path $lvl.FullName -Destination $backupLvl -Force
                            Write-Host "  [OK] Preserved original level0 -> $($lvl.Name).original" -ForegroundColor Green
                        }
                        $newBytes = [System.Text.Encoding]::ASCII.GetBytes($patchAppId)
                        [System.Array]::Copy($newBytes, 0, $lvlBytes, $foundIdx, 36)
                        Write-Host "  [SUCCESS] Patched Photon Fusion AppID -> $patchAppId in $($lvl.Name) at offset $foundIdx" -ForegroundColor Green

                        # Also patch paired Voice AppID at offset foundIdx + 40 if present
                        $voiceIdx = $foundIdx + 40
                        if ($patchVoiceId -and $patchVoiceId.Length -eq 36 -and $lvlBytes.Length -ge ($voiceIdx + 36)) {
                            $strAtVoice = [System.Text.Encoding]::ASCII.GetString($lvlBytes, $voiceIdx, 36)
                            if ($strAtVoice -match "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") {
                                $newVoiceBytes = [System.Text.Encoding]::ASCII.GetBytes($patchVoiceId)
                                [System.Array]::Copy($newVoiceBytes, 0, $lvlBytes, $voiceIdx, 36)
                                Write-Host "  [SUCCESS] Patched paired Photon Voice AppID -> $patchVoiceId in $($lvl.Name) at offset $voiceIdx" -ForegroundColor Green
                            }
                        }

                        [System.IO.File]::WriteAllBytes($lvl.FullName, $lvlBytes)
                    } else {
                        Write-Host "  [INFO] Photon Fusion AppID is PlaySide default or operating via UNAE SDR/P2P tunnel. (To use custom AppID, set AppIdFusion in ReFix.ini)" -ForegroundColor Gray
                    }
                }
            } catch {
                Write-Host "  [NOTICE] Error scanning $($lvl.Name): $_" -ForegroundColor Yellow
            }
        }
    }

    # ------------------------------------------------------------
    # Photon Fusion / IL2CPP Authentication Bypass Patch
    # For Dumb Ways to Build: force AuthType = CustomAuthenticationType.None (255)
    # and suppress Steam ticket injection so Photon connects anonymously without 403 / AUT-004
    # ------------------------------------------------------------
    $gameAssemblyPath = Join-Path $TargetDir "GameAssembly.dll"
    if (Test-Path $gameAssemblyPath) {
        try {
            $gaBytes = [System.IO.File]::ReadAllBytes($gameAssemblyPath)
            $offset1 = 0xB12CF0
            $offset2 = 0xB12FC0
            $offsetAuth = 0xB13737
            $offsetVoiceAuth = 0xB13A27
            $modified = $false

            if ($gaBytes.Length -gt ($offsetVoiceAuth + 10)) {
                $gaBackup = "$gameAssemblyPath.original"
                if (-not (Test-Path $gaBackup)) {
                    Copy-Item -Path $gameAssemblyPath -Destination $gaBackup -Force
                    Write-Host "  [OK] Preserved original GameAssembly.dll -> GameAssembly.dll.original" -ForegroundColor Green
                }

                # 1. Neutralize ApplyAuthenticationTicketParameters -> ret (0xC3)
                if ($gaBytes[$offset1] -eq 0x48 -and $gaBytes[$offset1 + 1] -eq 0x89) {
                    $gaBytes[$offset1] = [byte]0xC3
                    $modified = $true
                }
                if ($gaBytes[$offset2] -eq 0x48 -and $gaBytes[$offset2 + 1] -eq 0x89) {
                    $gaBytes[$offset2] = [byte]0xC3
                    $modified = $true
                }

                # 2. Force AuthType = CustomAuthenticationType.None (0xFF / 255) in CreateAuthenticationValues
                # Pattern: b0 01 eb 02 32 c0 88 43 10 -> b0 ff eb 02 b0 ff 88 43 10
                if ($gaBytes[$offsetAuth] -eq 0xB0) {
                    $gaBytes[$offsetAuth + 1] = [byte]0xFF
                    $gaBytes[$offsetAuth + 4] = [byte]0xB0
                    $gaBytes[$offsetAuth + 5] = [byte]0xFF
                    $modified = $true
                }

                # 3. Force AuthType = CustomAuthenticationType.None (0xFF / 255) in CreateVoiceAuthenticationValues
                if ($gaBytes[$offsetVoiceAuth] -eq 0xB0) {
                    $gaBytes[$offsetVoiceAuth + 1] = [byte]0xFF
                    $gaBytes[$offsetVoiceAuth + 4] = [byte]0xB0
                    $gaBytes[$offsetVoiceAuth + 5] = [byte]0xFF
                    $modified = $true
                }

                if ($modified) {
                    [System.IO.File]::WriteAllBytes($gameAssemblyPath, $gaBytes)
                    Write-Host "  [SUCCESS] Patched GameAssembly.dll: AuthType -> None (0xFF) & Ticket -> ret (Anonymous Photon Handshake)" -ForegroundColor Green
                }
            }
        } catch {
            Write-Host "  [NOTICE] Error inspecting GameAssembly.dll: $_" -ForegroundColor Yellow
        }
    }

    # For Mono games, apply Mono.Cecil patches across Managed assemblies
    $managedDir = Join-Path $TargetDir "*_Data\Managed"
    $managedFolders = Get-Item -Path $managedDir -ErrorAction SilentlyContinue

    $cecilPath = Join-Path $BinDir "bepinex\core\Mono.Cecil.dll"
    if (-not (Test-Path $cecilPath)) {
        $cecilItem = Get-ChildItem -Path $BinDir -Filter "Mono.Cecil.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cecilItem) { $cecilPath = $cecilItem.FullName }
    }

    if (Test-Path $cecilPath) {
        try { Add-Type -Path $cecilPath -ErrorAction SilentlyContinue } catch {}
    }

    foreach ($mFolder in $managedFolders) {
        $candidateDlls = Get-ChildItem -Path $mFolder.FullName -Filter "*.dll" -ErrorAction SilentlyContinue | 
            Where-Object { $_.Name -match "(?i)(steamworks|assembly-csharp|com\.rlabrecque|utilities)" }

        $resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
        $resolver.AddSearchDirectory($mFolder.FullName)

        foreach ($dllItem in $candidateDlls) {
            try {
                $readerParams = New-Object Mono.Cecil.ReaderParameters
                $readerParams.ReadWrite = $true
                $readerParams.AssemblyResolver = $resolver
                $asmDef = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllItem.FullName, $readerParams)
                $modified = $false

                # 1. Patch Steamworks.NativeMethods + Steamworks.SteamAPI (Fix SteamInternal_SteamAPI_Init missing export on emulators)
                $nativeType = $asmDef.MainModule.Types | Where-Object { $_.FullName -eq "Steamworks.NativeMethods" }
                $apiType = $asmDef.MainModule.Types | Where-Object { $_.FullName -eq "Steamworks.SteamAPI" }
                $ctxType = $asmDef.MainModule.Types | Where-Object { $_.FullName -eq "Steamworks.CSteamAPIContext" }
                $cbType = $asmDef.MainModule.Types | Where-Object { $_.FullName -eq "Steamworks.CallbackDispatcher" }

                if ($nativeType -and $apiType -and $OnlineMode -eq "goldberg") {
                    $initNative = $nativeType.Methods | Where-Object { $_.Name -eq "SteamInternal_SteamAPI_Init" }
                    $apiInit = $apiType.Methods | Where-Object { $_.Name -eq "Init" }
                    if ($initNative -and $apiInit) {
                        # Change P/Invoke entry point from SteamInternal_SteamAPI_Init to SteamAPI_Init
                        if ($initNative.PInvokeInfo -and $initNative.PInvokeInfo.EntryPoint -ne "SteamAPI_Init") {
                            $initNative.PInvokeInfo.EntryPoint = "SteamAPI_Init"
                            $initNative.Parameters.Clear()
                            $initNative.ReturnType = $asmDef.MainModule.TypeSystem.Boolean
                            $modified = $true
                        }

                        # Rewrite SteamAPI.Init to safely initialize emulator and all contexts
                        $ctxInit = if ($ctxType) { $ctxType.Methods | Where-Object { $_.Name -eq "Init" } } else { $null }
                        $cbInit = if ($cbType) { $cbType.Methods | Where-Object { $_.Name -eq "Initialize" } } else { $null }
                        $shutdownMethod = $apiType.Methods | Where-Object { $_.Name -eq "Shutdown" }
                        $testPlat = if ($shutdownMethod -and $shutdownMethod.HasBody -and $shutdownMethod.Body.Instructions.Count -gt 0) { $shutdownMethod.Body.Instructions[0].Operand } else { $null }

                        $apiInit.Body.Instructions.Clear()
                        $apiInit.Body.Variables.Clear()

                        $il = $apiInit.Body.GetILProcessor()
                        $lblFail = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
                        $lblRet = $il.Create([Mono.Cecil.Cil.OpCodes]::Ret)

                        if ($testPlat) { $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $testPlat)) }
                        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $initNative))
                        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Brfalse_S, $lblFail))
                        if ($ctxInit) {
                            $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $ctxInit))
                            $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Pop))
                        }
                        if ($cbInit) {
                            $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $cbInit))
                        }
                        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1))
                        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))
                        $il.Append($lblFail)
                        $il.Append($lblRet)
                        $modified = $true

                        # Rewrite SteamAPI.InitEx
                        $apiInitEx = $apiType.Methods | Where-Object { $_.Name -eq "InitEx" }
                        if ($apiInitEx) {
                            $apiInitEx.Body.Instructions.Clear()
                            $apiInitEx.Body.Variables.Clear()

                            $ilEx = $apiInitEx.Body.GetILProcessor()
                            $lblExFail = $ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
                            $lblExRet = $ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ret)

                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, ""))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Stind_Ref))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Call, $apiInit))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Brfalse_S, $lblExFail))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ret))
                            $ilEx.Append($lblExFail)
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "[Steamworks.NET] SteamAPI_Init() failed."))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Stind_Ref))
                            $ilEx.Append($ilEx.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1))
                            $ilEx.Append($lblExRet)
                            $modified = $true
                        }

                        Write-Host "  [OK] Rewrote SteamAPI.Init / InitEx in $($dllItem.Name) for native emulator compatibility" -ForegroundColor Green
                    }
                }

                # 2. Patch Steamworks.NET CSteamAPIContext::Init (fix Steam Timeline interface requirement)
                if ($ctxType) {
                    $ctxInitMethod = $ctxType.Methods | Where-Object { $_.Name -eq "Init" }
                    if ($ctxInitMethod -and $ctxInitMethod.HasBody) {
                        for ($i = 0; $i -lt $ctxInitMethod.Body.Instructions.Count; $i++) {
                            $instr = $ctxInitMethod.Body.Instructions[$i]
                            if ($instr.OpCode.Name -eq "ldstr" -and $instr.Operand -match "(?i)STEAMTIMELINE") {
                                for ($j = $i; $j -lt $ctxInitMethod.Body.Instructions.Count; $j++) {
                                    if ($ctxInitMethod.Body.Instructions[$j].OpCode.Name -eq "ldc.i4.0" -and $ctxInitMethod.Body.Instructions[$j+1].OpCode.Name -eq "ret") {
                                        $ctxInitMethod.Body.Instructions[$j].OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4_1
                                        $modified = $true
                                        Write-Host "  [OK] Patched CSteamAPIContext::Init in $($dllItem.Name) for Steam Timeline compatibility" -ForegroundColor Green
                                        break
                                    }
                                }
                                break
                            }
                        }
                    }
                }

                # 3. Patch Assembly-CSharp.dll SteamManager + SteamP2PManager
                $smType = $asmDef.MainModule.Types | Where-Object { $_.Name -eq "SteamManager" }
                if ($smType) {
                    # Disable RestartAppIfNecessary application quitting
                    $awakeMethod = $smType.Methods | Where-Object { $_.Name -eq "Awake" }
                    if ($awakeMethod -and $awakeMethod.HasBody) {
                        for ($i = 0; $i -lt $awakeMethod.Body.Instructions.Count; $i++) {
                            $instr = $awakeMethod.Body.Instructions[$i]
                            if ($instr.OpCode.Name -eq "call" -and $instr.Operand.ToString() -match "RestartAppIfNecessary") {
                                for ($j = $i; $j -lt [Math]::Min($i + 6, $awakeMethod.Body.Instructions.Count); $j++) {
                                    if ($awakeMethod.Body.Instructions[$j].OpCode.Name -eq "call" -and $awakeMethod.Body.Instructions[$j].Operand.ToString() -match "Application::Quit") {
                                        $awakeMethod.Body.Instructions[$j].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                        $awakeMethod.Body.Instructions[$j].Operand = $null
                                        $modified = $true
                                        Write-Host "  [OK] Neutralized RestartAppIfNecessary Application.Quit in $($dllItem.Name)" -ForegroundColor Green
                                        break
                                    }
                                }
                                break
                            }
                        }
                    }

                    # Patch Photon CustomAuth if in photon mode
                    if ($OnlineMode -eq "photon") {
                        $authMethod = $smType.Methods | Where-Object { $_.Name -eq "SendSteamAuthTicket" }
                        if ($authMethod -and $authMethod.HasBody) {
                            $alreadyPatched = $false
                            foreach ($instr in $authMethod.Body.Instructions) {
                                if ($instr.OpCode.Name -eq "ldstr" -and $instr.Operand -eq "appid") { $alreadyPatched = $true; break }
                            }
                            if (-not $alreadyPatched) {
                                $targetInstr = $null
                                $getAuthValues = $null
                                foreach ($instr in $authMethod.Body.Instructions) {
                                    if ($instr.OpCode.Name -eq "callvirt" -and $instr.Operand.Name -eq "AddAuthParameter") { $targetInstr = $instr }
                                    if ($instr.OpCode.Name -eq "call" -and $instr.Operand.Name -eq "get_AuthValues") { $getAuthValues = $instr.Operand }
                                }
                                if ($targetInstr -and $getAuthValues) {
                                    $il = $authMethod.Body.GetILProcessor()
                                    $i1 = $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $getAuthValues)
                                    $i2 = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "appid")
                                    $i3 = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, "480")
                                    $i4 = $il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $targetInstr.Operand)

                                    $il.InsertAfter($targetInstr, $i1)
                                    $il.InsertAfter($i1, $i2)
                                    $il.InsertAfter($i2, $i3)
                                    $il.InsertAfter($i3, $i4)

                                    $modified = $true
                                    Write-Host "  [OK] Injected appid=480 for Photon CustomAuth in $($dllItem.Name)" -ForegroundColor Green
                                }
                            } else {
                                Write-Host "  [INFO] Photon CustomAuth appid=480 already patched in $($dllItem.Name)" -ForegroundColor Yellow
                            }
                        }
                    }
                }

                # Patch SteamP2PManager in Assembly-CSharp.dll (Neutralize premature InCurrentLobby rejections)
                $p2pType = $asmDef.MainModule.Types | Where-Object { $_.Name -eq "SteamP2PManager" }
                if ($p2pType) {
                    $osrMethod = $p2pType.Methods | Where-Object { $_.Name -eq "OnSessionRequest" }
                    if ($osrMethod -and $osrMethod.HasBody) {
                        for ($i = 0; $i -lt $osrMethod.Body.Instructions.Count; $i++) {
                            $instr = $osrMethod.Body.Instructions[$i]
                            if ($instr.OpCode.Name -eq "callvirt" -and $instr.Operand -and $instr.Operand.ToString() -like "*InCurrentLobby*") {
                                $osrMethod.Body.Instructions[$i - 2].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $osrMethod.Body.Instructions[$i - 2].Operand = $null
                                $osrMethod.Body.Instructions[$i - 1].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $osrMethod.Body.Instructions[$i - 1].Operand = $null
                                $osrMethod.Body.Instructions[$i].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $osrMethod.Body.Instructions[$i].Operand = $null
                                $osrMethod.Body.Instructions[$i + 1].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $osrMethod.Body.Instructions[$i + 1].Operand = $null
                                $modified = $true
                                Write-Host "  [OK] Patched SteamP2PManager::OnSessionRequest (Neutralized InCurrentLobby race condition)" -ForegroundColor Green
                                break
                            }
                        }
                    }

                    $prmMethod = $p2pType.Methods | Where-Object { $_.Name -eq "ProcessReceivedMessage" }
                    if ($prmMethod -and $prmMethod.HasBody) {
                        for ($i = 0; $i -lt $prmMethod.Body.Instructions.Count; $i++) {
                            $instr = $prmMethod.Body.Instructions[$i]
                            if ($instr.OpCode.Name -eq "callvirt" -and $instr.Operand -and $instr.Operand.ToString() -like "*InCurrentLobby*") {
                                $prmMethod.Body.Instructions[$i - 2].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $prmMethod.Body.Instructions[$i - 2].Operand = $null
                                $prmMethod.Body.Instructions[$i - 1].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $prmMethod.Body.Instructions[$i - 1].Operand = $null
                                $prmMethod.Body.Instructions[$i].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $prmMethod.Body.Instructions[$i].Operand = $null
                                $prmMethod.Body.Instructions[$i + 1].OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
                                $prmMethod.Body.Instructions[$i + 1].Operand = $null
                                $modified = $true
                                Write-Host "  [OK] Patched SteamP2PManager::ProcessReceivedMessage (Prevented premature player kick)" -ForegroundColor Green
                                break
                            }
                        }
                    }
                }

                # 4. Patch Utilities.dll (Map<T1, T2>.Remove safe TryGetValue)
                $mapType = $asmDef.MainModule.Types | Where-Object { $_.Name -eq 'Map`2' }
                if ($mapType) {
                    $fwdField = $mapType.Fields | Where-Object { $_.Name -eq '_forward' }
                    $revField = $mapType.Fields | Where-Object { $_.Name -eq '_reverse' }
                    if ($fwdField -and $revField) {
                        foreach ($m in $mapType.Methods) {
                            if ($m.Name -eq 'Remove' -and $m.Parameters.Count -eq 1) {
                                $paramType = $m.Parameters[0].ParameterType
                                $isT1 = ($paramType.Name -eq 'T1')
                                $dictSourceField = if ($isT1) { $fwdField } else { $revField }
                                $dictTargetField = if ($isT1) { $revField } else { $fwdField }
                                $targetType = if ($isT1) { $mapType.GenericParameters[1] } else { $mapType.GenericParameters[0] }

                                $m.Body.Instructions.Clear()
                                $m.Body.Variables.Clear()

                                $var0 = New-Object Mono.Cecil.Cil.VariableDefinition($targetType)
                                $m.Body.Variables.Add($var0)

                                $il = $m.Body.GetILProcessor()
                                $pDef1 = New-Object Mono.Cecil.ParameterDefinition($paramType)
                                $byRefTarget = New-Object Mono.Cecil.ByReferenceType($targetType)
                                $pDef2 = New-Object Mono.Cecil.ParameterDefinition($byRefTarget)

                                $tryGetRef = New-Object Mono.Cecil.MethodReference('TryGetValue', $asmDef.MainModule.TypeSystem.Boolean, $dictSourceField.FieldType)
                                $tryGetRef.HasThis = $true
                                $tryGetRef.Parameters.Add($pDef1)
                                $tryGetRef.Parameters.Add($pDef2)

                                $pDefTarget = New-Object Mono.Cecil.ParameterDefinition($targetType)
                                $remTargetRef = New-Object Mono.Cecil.MethodReference('Remove', $asmDef.MainModule.TypeSystem.Boolean, $dictTargetField.FieldType)
                                $remTargetRef.HasThis = $true
                                $remTargetRef.Parameters.Add($pDefTarget)

                                $pDefSource = New-Object Mono.Cecil.ParameterDefinition($paramType)
                                $remSourceRef = New-Object Mono.Cecil.MethodReference('Remove', $asmDef.MainModule.TypeSystem.Boolean, $dictSourceField.FieldType)
                                $remSourceRef.HasThis = $true
                                $remSourceRef.Parameters.Add($pDefSource)

                                $lblFail = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
                                $lblRet = $il.Create([Mono.Cecil.Cil.OpCodes]::Ret)

                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $dictSourceField))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldloca_S, $var0))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $tryGetRef))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Brfalse_S, $lblFail))

                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $dictTargetField))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldloc_0))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $remTargetRef))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Pop))

                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $dictSourceField))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $remSourceRef))
                                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))

                                $il.Append($lblFail)
                                $il.Append($lblRet)

                                $modified = $true
                                Write-Host "  [OK] Patched Map::Remove($($paramType.Name)) in $($dllItem.Name) with safe TryGetValue" -ForegroundColor Green
                            }
                        }
                    }
                }

                if ($modified) {
                    $origPath = "$($dllItem.FullName).orig"
                    if (-not (Test-Path $origPath)) {
                        Copy-Item -Path $dllItem.FullName -Destination $origPath -Force
                        Write-Host "  [OK] Backed up $($dllItem.Name) -> $($dllItem.Name).orig" -ForegroundColor Green
                    }
                    $asmDef.Write()
                }
                if ($asmDef) { $asmDef.Dispose() }
            } catch {
                # Skip non-managed or unreadable DLL
            }
        }
    }

    # --- Photon mode: Deploy BepInEx + UniversalPhotonFix ---
    if ($OnlineMode -eq "photon") {
        if ($EngineType -ne "Unity") {
            Write-Host "  [ERROR] Photon mode is only supported for Unity games!" -ForegroundColor Red
            exit 1
        }
        Write-Host "  [Photon] Deploying BepInEx framework + UniversalPhotonFix plugin..." -ForegroundColor Cyan

        $bepinexSrc = Join-Path $BinDir "bepinex"
        if (-not (Test-Path $bepinexSrc)) {
            Write-Host "  [ERROR] BepInEx source not found at: $bepinexSrc" -ForegroundColor Red
            exit 1
        }

        # Deploy winhttp.dll (doorstop proxy) to game root
        $winhttpSrc = Join-Path $bepinexSrc "winhttp.dll"
        if (Test-Path $winhttpSrc) {
            Copy-Item -Path $winhttpSrc -Destination $TargetDir -Force
            Write-Host "  [OK] Deployed winhttp.dll (BepInEx doorstop) to game root" -ForegroundColor Green
        }

        # Deploy doorstop_config.ini to game root
        $doorstopSrc = Join-Path $bepinexSrc "doorstop_config.ini"
        if (Test-Path $doorstopSrc) {
            Copy-Item -Path $doorstopSrc -Destination $TargetDir -Force
            Write-Host "  [OK] Deployed doorstop_config.ini to game root" -ForegroundColor Green
        }

        # Deploy BepInEx/core/ directory
        $coreSrc = Join-Path $bepinexSrc "core"
        $coreDst = Join-Path $TargetDir "BepInEx\core"
        if (-not (Test-Path $coreDst)) { New-Item -ItemType Directory -Path $coreDst -Force | Out-Null }
        Copy-Item -Path "$coreSrc\*" -Destination $coreDst -Recurse -Force
        Write-Host "  [OK] Deployed BepInEx\core\ ($(Get-ChildItem $coreDst -File | Measure-Object | Select-Object -Exp Count) files)" -ForegroundColor Green

        # Deploy UniversalPhotonFix plugin
        $pluginsSrc = Join-Path $bepinexSrc "plugins"
        $pluginsDst = Join-Path $TargetDir "BepInEx\plugins"
        if (-not (Test-Path $pluginsDst)) { New-Item -ItemType Directory -Path $pluginsDst -Force | Out-Null }
        $pluginSrc = Join-Path $pluginsSrc "UniversalPhotonFix.dll"
        if (Test-Path $pluginSrc) {
            Copy-Item -Path $pluginSrc -Destination $pluginsDst -Force
            Write-Host "  [OK] Deployed UniversalPhotonFix.dll plugin" -ForegroundColor Green
        } else {
            $nekogiriSrc = Join-Path $pluginsSrc "NekogiriFix.dll"
            if (Test-Path $nekogiriSrc) {
                Copy-Item -Path $nekogiriSrc -Destination $pluginsDst -Force
                Write-Host "  [OK] Deployed NekogiriFix.dll plugin" -ForegroundColor Green
            } else {
                Write-Host "  [WARNING] UniversalPhotonFix.dll not found in $pluginsSrc" -ForegroundColor Yellow
            }
        }

        # Create BepInEx\config directory
        $configDst = Join-Path $TargetDir "BepInEx\config"
        if (-not (Test-Path $configDst)) { New-Item -ItemType Directory -Path $configDst -Force | Out-Null }

        # Generate Kirigiri.ini from ReFix.ini Photon settings
        Write-Host "  [Photon] Generating Kirigiri.ini from Photon configuration..." -ForegroundColor Cyan

        $reFixIni = Join-Path $ExeDir "ReFix.ini"
        $steamAppIdValue = "480"
        $effectiveRealtime = if ($PhotonAppId) { $PhotonAppId } else { $AppIdRealtime }
        $effectiveVoice = $AppIdVoice
        $effectiveFusion = $AppIdFusion
        $effectiveRegion = if ($PhotonRegion) { $PhotonRegion } else { $DefaultRegion }

        if (Test-Path $reFixIni) {
            $iniContent = Get-Content $reFixIni -ErrorAction SilentlyContinue
            foreach ($line in $iniContent) {
                if ($line -match "^\s*MaskAppId\s*=\s*(.+)") { $steamAppIdValue = $Matches[1].Trim() }
                if (-not $effectiveRealtime -and $line -match "^\s*AppIdRealtime\s*=\s*(.+)") { $effectiveRealtime = $Matches[1].Trim() }
                if (-not $effectiveVoice -and $line -match "^\s*AppIdVoice\s*=\s*(.+)") { $effectiveVoice = $Matches[1].Trim() }
                if (-not $effectiveFusion -and $line -match "^\s*AppIdFusion\s*=\s*(.+)") { $effectiveFusion = $Matches[1].Trim() }
                if ($line -match "^\s*DefaultRegion\s*=\s*(.+)") { $effectiveRegion = $Matches[1].Trim() }
            }
        }

        $kirigiriPath = Join-Path $TargetDir "Kirigiri.ini"
        $kirigiriContent = @"
; ============================================================
; Kirigiri.ini - UniversalPhotonFix Configuration
; ============================================================
; Auto-generated by ReFix AutoDeploy (Photon mode)
;
; This file is read by BepInEx/UniversalPhotonFix at game startup.
; To change Photon settings, edit ReFix.ini [Photon] section
; and re-run AutoDeploy, or edit this file directly.
;
; Get your free Photon AppID at:
;   https://dashboard.photonengine.com
;   Dashboard > Create App > Select "Photon Realtime" (NOT PUN2 or Fusion)
; ============================================================

[Settings]
SteamAppId=$steamAppIdValue
AppIdRealtime=$effectiveRealtime
AppIdVoice=$effectiveVoice
AppIdFusion=$effectiveFusion
Auth=None
FixedRegion=$effectiveRegion
"@
        [System.IO.File]::WriteAllText($kirigiriPath, $kirigiriContent)
        Write-Host "  [OK] Generated Kirigiri.ini (AppIdRealtime=$effectiveRealtime, FixedRegion=$effectiveRegion)" -ForegroundColor Green

        if (-not $effectiveRealtime) {
            Write-Host "  [REMINDER] Photon AppIdRealtime is empty!" -ForegroundColor Yellow
            Write-Host "  [REMINDER] Edit Kirigiri.ini or ReFix.ini [Photon] section before launching the game." -ForegroundColor Yellow
        }
    }

} elseif ($EngineType -eq "Godot") {
    Write-Host "[4/6] Processing Godot deployment (mode: $OnlineMode)..." -ForegroundColor Cyan

    $reFixIni = Join-Path $ExeDir "ReFix.ini"
    $targetAppId = "480"
    if (Test-Path -LiteralPath $reFixIni) {
        $iniLines = Get-Content -LiteralPath $reFixIni -ErrorAction SilentlyContinue
        foreach ($line in $iniLines) {
            if ($OnlineMode -eq "valve") {
                if ($line -match "^\s*MaskAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^\s*RealAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }

    # Place steam_appid.txt in Godot root folder
    try {
        $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
        [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
        if (-not (Test-Path -LiteralPath $exeAppIdPath)) { throw "Failed to write steam_appid.txt" }
        Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in Godot root $ExeDir" -ForegroundColor Green
    } catch {
        Write-Host "  [ERROR] Failed to write steam_appid.txt: $_" -ForegroundColor Red
        exit 1
    }

    $winmmPath = Join-Path $BinDir "winmm.dll"
    if (Test-Path -LiteralPath $winmmPath) {
        $targetWinmm = Join-Path $ExeDir "winmm.dll"
        $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
        try {
            if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item -LiteralPath $targetWinmm).Length -ne (Get-Item -LiteralPath $winmmPath).Length)) {
                Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
            }
            Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force -ErrorAction Stop
            if (-not (Test-Path -LiteralPath $targetWinmm)) { throw "Failed to copy winmm.dll" }
            Write-Host "  [OK] Deployed ReFix winmm.dll proxy to Godot root folder $ExeDir" -ForegroundColor Green
        } catch {
            Write-Host "  [ERROR] Failed to deploy winmm.dll: $_" -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "  [OK] Godot adapter ready (GodotSteam & SteamMultiplayerPeer supported)" -ForegroundColor Green

} elseif ($EngineType -eq "Unreal") {
    Write-Host "[4/6] Processing Unreal Engine deployment (mode: $OnlineMode)..." -ForegroundColor Cyan
    
    $reFixIni = Join-Path $ExeDir "ReFix.ini"
    $targetAppId = "480"
    if (Test-Path -LiteralPath $reFixIni) {
        $iniLines = Get-Content -LiteralPath $reFixIni -ErrorAction SilentlyContinue
        foreach ($line in $iniLines) {
            if ($OnlineMode -eq "valve") {
                if ($line -match "^\s*MaskAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^\s*RealAppId\s*=\s*(.+)") { $targetAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }

    # 1. Place steam_appid.txt in ExeDir and TargetDir root
    try {
        $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
        [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
        if (-not (Test-Path -LiteralPath $exeAppIdPath)) { throw "Failed to write steam_appid.txt in ExeDir" }
        Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $ExeDir" -ForegroundColor Green

        $rootAppIdPath = Join-Path $TargetDir "steam_appid.txt"
        if ($rootAppIdPath -ne $exeAppIdPath) {
            [System.IO.File]::WriteAllText($rootAppIdPath, "$targetAppId`r`n")
        }
    } catch {
        Write-Host "  [ERROR] Failed to write steam_appid.txt: $_" -ForegroundColor Red
        exit 1
    }

    # 2. Deploy winmm.dll proxy to Unreal root folder ExeDir
    $winmmPath = Join-Path $BinDir "winmm.dll"
    if (Test-Path -LiteralPath $winmmPath) {
        $targetWinmm = Join-Path $ExeDir "winmm.dll"
        $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
        try {
            if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item -LiteralPath $targetWinmm).Length -ne (Get-Item -LiteralPath $winmmPath).Length)) {
                Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
            }
            Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force -ErrorAction Stop
            if (-not (Test-Path -LiteralPath $targetWinmm)) { throw "Failed to copy winmm.dll" }
            Write-Host "  [OK] Deployed ReFix winmm.dll proxy to Unreal root folder $ExeDir" -ForegroundColor Green
        } catch {
            Write-Host "  [ERROR] Failed to deploy winmm.dll to $($ExeDir): $_" -ForegroundColor Red
            exit 1
        }
    }
} else {
    Write-Host "[4/6] Processing Native/Custom deployment (mode: $OnlineMode)..." -ForegroundColor Cyan
    $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
    $targetAppId = if ($OnlineMode -eq "valve") { $MaskAppId } else { $finalRealAppId }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }
    try {
        [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
        if (-not (Test-Path -LiteralPath $exeAppIdPath)) { throw "Failed to write steam_appid.txt" }
        Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $ExeDir" -ForegroundColor Green

        $rootAppIdPath = Join-Path $TargetDir "steam_appid.txt"
        if ($rootAppIdPath -ne $exeAppIdPath) {
            [System.IO.File]::WriteAllText($rootAppIdPath, "$targetAppId`r`n")
        }
    } catch {
        Write-Host "  [ERROR] Failed to write steam_appid.txt: $_" -ForegroundColor Red
        exit 1
    }
}

# Deploy shortcut helper scripts for Valve mode
if ($OnlineMode -eq "valve") {
    $srcPs1 = Join-Path $BinDir "add_steam_shortcut.ps1"
    $srcBat = Join-Path $BinDir "Install_ReFix_Steam_Shortcut.bat"
    if (Test-Path -LiteralPath $srcPs1) {
        Copy-Item -LiteralPath $srcPs1 -Destination (Join-Path $ExeDir "add_steam_shortcut.ps1") -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $srcBat) {
        Copy-Item -LiteralPath $srcBat -Destination (Join-Path $ExeDir "Install_ReFix_Steam_Shortcut.bat") -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================
# Step 5: Universal Epic Online Services (EOS) & Redpoint Deployment
# (Applies to all engines: Unity, Unreal, Godot, Custom)
# ============================================================
Write-Host "[5/6] Checking for Epic Online Services (EOS / Redbone)..." -ForegroundColor Cyan

$eosProxyPath = Join-Path $BinDir "EOSSDK-Win64-Shipping.dll"
$redboneProxyPath = Join-Path $BinDir "RedboneEOS.dll"
if ((-not (Test-Path -LiteralPath $redboneProxyPath)) -and (Test-Path -LiteralPath $eosProxyPath)) {
    $redboneProxyPath = $eosProxyPath
}

$eosDlls = Get-ChildItem -LiteralPath $TargetDir -Filter "EOSSDK-Win64-Shipping.dll" -Recurse -File -ErrorAction SilentlyContinue
$redboneDlls = Get-ChildItem -LiteralPath $TargetDir -Filter "RedboneEOS*.dll" -Recurse -File -ErrorAction SilentlyContinue

$eosDirs = @()
foreach ($dll in $eosDlls) {
    if ($eosDirs -notcontains $dll.DirectoryName) { $eosDirs += $dll.DirectoryName }
}
foreach ($dll in $redboneDlls) {
    if ($eosDirs -notcontains $dll.DirectoryName) { $eosDirs += $dll.DirectoryName }
}

if ($eosDirs.Count -eq 0) {
    $redpointFolder = Get-ChildItem -LiteralPath $TargetDir -Filter "RedpointEOS" -Directory -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($redpointFolder) { $eosDirs += $redpointFolder.FullName }
}

if ($eosDirs.Count -gt 0 -and (Test-Path -LiteralPath $eosProxyPath)) {
    $proxySize = (Get-Item -LiteralPath $eosProxyPath).Length
    foreach ($dir in $eosDirs) {
        $eosOriginal = Join-Path $dir "EOSSDK_original.dll"
        $eosPath = Join-Path $dir "EOSSDK-Win64-Shipping.dll"

        if (Test-Path -LiteralPath $eosPath) {
            $curSize = (Get-Item -LiteralPath $eosPath).Length
            if ($curSize -ne $proxySize -and (-not (Test-Path -LiteralPath $eosOriginal))) {
                Rename-Item -LiteralPath $eosPath -NewName "EOSSDK_original.dll" -Force
                Write-Host "  [OK] Preserved original EOSSDK -> EOSSDK_original.dll in $dir" -ForegroundColor Green
            }
        }
        Copy-Item -LiteralPath $eosProxyPath -Destination $eosPath -Force
        Write-Host "  [OK] Deployed Dual-Mode EOSSDK-Win64-Shipping.dll proxy to $dir" -ForegroundColor Green

        # If RedboneEOS.dll exists, replace with proxy
        $targetRedbone = Join-Path $dir "RedboneEOS.dll"
        if (Test-Path -LiteralPath $targetRedbone) {
            Copy-Item -LiteralPath $redboneProxyPath -Destination $targetRedbone -Force
            Write-Host "  [OK] Deployed RedboneEOS.dll proxy to $dir" -ForegroundColor Green
        }
    }

    # If EOS was found in a subfolder (like Unity Plugins\x86_64 or RedpointEOS), clean up redundant proxy from ExeDir
    if ($eosDirs.Count -gt 0 -and ($eosDirs -notcontains $ExeDir)) {
        $exeEos = Join-Path $ExeDir "EOSSDK-Win64-Shipping.dll"
        $exeEosOrig = Join-Path $ExeDir "EOSSDK_original.dll"
        if ((Test-Path -LiteralPath $exeEos) -and (-not (Test-Path -LiteralPath $exeEosOrig))) {
            if ((Get-Item -LiteralPath $exeEos).Length -eq $proxySize) {
                Remove-Item -LiteralPath $exeEos -Force -ErrorAction SilentlyContinue
                Write-Host "  [OK] Cleaned redundant EOS proxy from $ExeDir" -ForegroundColor Green
            }
        }
    }
} else {
    Write-Host "  [INFO] No Epic Online Services (EOS) libraries detected in game files." -ForegroundColor Gray
}

# ============================================================
# Step 6: Final Verification & Post-Deploy Validation (FASE 6)
# ============================================================
function Verify-ReFixDeployment {
    param(
        [string]$TargetDir,
        [string]$ExeDir,
        [string]$BinDir,
        [string]$OnlineMode,
        [string]$EngineType,
        [string]$GameName,
        [array]$PluginDirs,
        [array]$PluginDirs32,
        [bool]$IsX86
    )

    Write-Host "`n============================================================" -ForegroundColor Cyan
    Write-Host " [POST-DEPLOY VERIFICATION] Validating Deployment Integrity..." -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan

    $errors = @()

    # 1. Target Directory & Exe Directory
    if (-not (Test-Path -LiteralPath $TargetDir)) {
        $errors += "Target directory does not exist: $TargetDir"
    }
    if (-not (Test-Path -LiteralPath $ExeDir)) {
        $errors += "Executable directory does not exist: $ExeDir"
    }

    # 2. ReFix.ini check (Mandatory for all deployments)
    $iniPath = Join-Path $ExeDir "ReFix.ini"
    if (-not (Test-Path -LiteralPath $iniPath)) {
        $errors += "Mandatory ReFix.ini missing in $ExeDir"
    } else {
        $iniLen = (Get-Item -LiteralPath $iniPath).Length
        if ($iniLen -le 0) {
            $errors += "ReFix.ini in $ExeDir is empty (0 bytes)"
        } else {
            Write-Host "  [VERIFY OK] ReFix.ini verified in $ExeDir ($iniLen bytes)" -ForegroundColor Green
        }
    }

    # 3. steam_appid.txt check (Mandatory)
    $appIdPath = Join-Path $ExeDir "steam_appid.txt"
    if (-not (Test-Path -LiteralPath $appIdPath)) {
        $foundInPlugin = $false
        foreach ($p in $PluginDirs) {
            if (Test-Path -LiteralPath (Join-Path $p "steam_appid.txt")) { $foundInPlugin = $true; break }
        }
        if (-not $foundInPlugin) {
            $errors += "Mandatory steam_appid.txt missing in $ExeDir and plugin directories"
        }
    } else {
        $appIdContent = (Get-Content -LiteralPath $appIdPath -Raw -ErrorAction SilentlyContinue)
        if (-not $appIdContent -or $appIdContent.Trim() -eq "") {
            $errors += "steam_appid.txt in $ExeDir is empty"
        } else {
            Write-Host "  [VERIFY OK] steam_appid.txt verified ($($appIdContent.Trim()))" -ForegroundColor Green
        }
    }

    # 4. Steam API DLL and Backup verification
    if (-not $IsX86) {
        $proxyPath = Join-Path $BinDir "steam_api64.dll"
        $expectedProxySize = if (Test-Path -LiteralPath $proxyPath) { (Get-Item -LiteralPath $proxyPath).Length } else { 0 }

        foreach ($pDir in $PluginDirs) {
            $steamDllPath = Join-Path $pDir "steam_api64.dll"
            if (-not (Test-Path -LiteralPath $steamDllPath)) {
                $errors += "steam_api64.dll missing in $pDir"
            } else {
                $dllSize = (Get-Item -LiteralPath $steamDllPath).Length
                if ($expectedProxySize -gt 0 -and $dllSize -ne $expectedProxySize) {
                    $errors += "steam_api64.dll size mismatch in $pDir (Expected $expectedProxySize, got $dllSize)"
                } else {
                    Write-Host "  [VERIFY OK] steam_api64.dll proxy verified in $pDir ($dllSize bytes)" -ForegroundColor Green
                }
            }

            # Check backup
            $valveBackup = Join-Path $pDir "steam_api64_valve.dll"
            $origBackup  = Join-Path $pDir "steam_api64_original.dll"
            if ((-not (Test-Path -LiteralPath $valveBackup)) -and (-not (Test-Path -LiteralPath $origBackup))) {
                $errors += "Mandatory original/backend DLL backup missing in $pDir (neither steam_api64_valve.dll nor steam_api64_original.dll exists)"
            } else {
                $bkName = if (Test-Path -LiteralPath $valveBackup) { "steam_api64_valve.dll" } else { "steam_api64_original.dll" }
                Write-Host "  [VERIFY OK] Backup DLL $bkName verified in $pDir" -ForegroundColor Green
            }
        }
    } else {
        # x86 game verification
        $proxyPath32 = Join-Path $BinDir "x86\steam_api.dll"
        $expectedProxy32Size = if (Test-Path -LiteralPath $proxyPath32) { (Get-Item -LiteralPath $proxyPath32).Length } else { 0 }

        foreach ($pDir32 in $PluginDirs32) {
            $steamDllPath32 = Join-Path $pDir32 "steam_api.dll"
            if (-not (Test-Path -LiteralPath $steamDllPath32)) {
                $errors += "x86 steam_api.dll missing in $pDir32"
            } else {
                $dllSize32 = (Get-Item -LiteralPath $steamDllPath32).Length
                Write-Host "  [VERIFY OK] x86 steam_api.dll proxy verified in $pDir32 ($dllSize32 bytes)" -ForegroundColor Green
            }

            $bk32a = Join-Path $pDir32 "steam_api_o.dll"
            $bk32b = Join-Path $pDir32 "steam_api_original.dll"
            $bk32c = Join-Path $pDir32 "steam_api_valve.dll"
            if ((-not (Test-Path -LiteralPath $bk32a)) -and (-not (Test-Path -LiteralPath $bk32b)) -and (-not (Test-Path -LiteralPath $bk32c))) {
                $errors += "Mandatory x86 backup DLL missing in $pDir32"
            } else {
                Write-Host "  [VERIFY OK] x86 backup DLL verified in $pDir32" -ForegroundColor Green
            }
        }
    }

    # 5. Loader proxy verification (winmm.dll)
    # Required for Valve mode on 64-bit games
    if ($OnlineMode -eq "valve" -and (-not $IsX86)) {
        $winmmPath = Join-Path $BinDir "winmm.dll"
        if (Test-Path -LiteralPath $winmmPath) {
            $destWinmm = Join-Path $ExeDir "winmm.dll"
            if (-not (Test-Path -LiteralPath $destWinmm)) {
                $errors += "Mandatory winmm.dll proxy missing in $ExeDir (required for Valve mode overlay injection)"
            } else {
                $wSize = (Get-Item -LiteralPath $destWinmm).Length
                $expectedWSize = (Get-Item -LiteralPath $winmmPath).Length
                if ($wSize -ne $expectedWSize) {
                    $errors += "winmm.dll in $ExeDir size mismatch (Expected $expectedWSize, got $wSize)"
                } else {
                    Write-Host "  [VERIFY OK] winmm.dll proxy verified in $ExeDir ($wSize bytes)" -ForegroundColor Green
                }
            }
        }
    }

    # 6. Goldberg mode specific verification
    if ($OnlineMode -in @("goldberg", "offline", "lan")) {
        $settingsDir = Join-Path $ExeDir "steam_settings"
        if (-not (Test-Path -LiteralPath $settingsDir)) {
            $errors += "steam_settings directory missing in $ExeDir"
        } else {
            $cfgApp = Join-Path $settingsDir "configs.app.ini"
            $txtAppId = Join-Path $settingsDir "steam_appid.txt"
            if ((-not (Test-Path -LiteralPath $cfgApp)) -and (-not (Test-Path -LiteralPath $txtAppId))) {
                $errors += "steam_settings configuration files missing in $settingsDir"
            } else {
                Write-Host "  [VERIFY OK] steam_settings configuration verified in $settingsDir" -ForegroundColor Green
            }
        }
    }

    # 7. Final verdict
    if ($errors.Count -gt 0) {
        Write-Host "`n[FATAL VERIFICATION FAILURE] Deployment failed validation checks:" -ForegroundColor Red
        foreach ($err in $errors) {
            Write-Host "  [-] $err" -ForegroundColor Red
        }
        Write-Host "============================================================" -ForegroundColor Red
        return $false
    } else {
        Write-Host "`n  [SUCCESS] All mandatory binaries, backups, configurations, and proxies verified successfully!" -ForegroundColor Green
        Write-Host "============================================================" -ForegroundColor Cyan
        return $true
    }
}

$verifySuccess = Verify-ReFixDeployment `
    -TargetDir $TargetDir `
    -ExeDir $ExeDir `
    -BinDir $BinDir `
    -OnlineMode $OnlineMode `
    -EngineType $EngineType `
    -GameName $GameName `
    -PluginDirs $pluginDirs `
    -PluginDirs32 $pluginDirs32 `
    -IsX86 $isX86Game

if (-not $verifySuccess) {
    Write-Host "[FATAL ERROR] Post-deploy verification failed! Mandatory files are missing or corrupted." -ForegroundColor Red
    exit 1
}

Write-Host "  [SUCCESS] ReFix deployment verified successfully for $GameName!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Cyan
exit 0

