param(
    [string]$TargetDir,
    [string]$BinDir,
    [string]$ExeDir,
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
    [string]$CustomBroadcasts = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
if (-not $TargetDir -and $env:BLUESTAR_TARGET_DIR) { $TargetDir = $env:BLUESTAR_TARGET_DIR }
if (-not $BinDir -and $env:BLUESTAR_BIN_DIR) { $BinDir = $env:BLUESTAR_BIN_DIR }
if (-not $ExeDir -and $env:BLUESTAR_EXE_DIR) { $ExeDir = $env:BLUESTAR_EXE_DIR }
if (-not $GameName -and $env:BLUESTAR_GAME_NAME) { $GameName = $env:BLUESTAR_GAME_NAME }
if (-not $UserName -and $env:BLUESTAR_USER_NAME) { $UserName = $env:BLUESTAR_USER_NAME }

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$OutputEncoding = [System.Text.Encoding]::UTF8



# Clean paths by trimming trailing quotes/slashes
if ($TargetDir) { $TargetDir = $TargetDir.TrimEnd('\').Trim('"') }
if ($BinDir)    { $BinDir    = $BinDir.TrimEnd('\').Trim('"') }
if ($ExeDir)    { $ExeDir    = $ExeDir.TrimEnd('\').Trim('"') }
if (-not $ExeDir) { $ExeDir  = $TargetDir }

Write-Host "`n============================================================" -ForegroundColor Cyan
Write-Host " [ReFix Deploy Engine] Target: $TargetDir" -ForegroundColor Cyan
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
# Helper: SteamStub DRM Detector & Automatic Unpacker
# ------------------------------------------------------------
function Unpack-SteamStubIfProtected {
    param(
        [string]$TargetFolder,
        [string]$ToolsBinDir
    )

    $steamlessCli = Join-Path $ToolsBinDir "tools\Steamless\Steamless.CLI.exe"
    if (-not (Test-Path -LiteralPath $steamlessCli)) {
        return
    }

    $exeFiles = Get-ChildItem -LiteralPath $TargetFolder -Filter "*.exe" -File -Recurse -ErrorAction SilentlyContinue | 
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
                if (-not (Test-Path -LiteralPath $backupPath)) {
                    Copy-Item -LiteralPath $exe.FullName -Destination $backupPath -Force
                    Write-Host "  [OK] Preserved original protected binary -> $($exe.BaseName).steamstub$($exe.Extension)" -ForegroundColor Green
                }

                $proc = Start-Process -FilePath $steamlessCli -ArgumentList "`"$($exe.FullName)`"" -WorkingDirectory $exe.DirectoryName -NoNewWindow -Wait -PassThru
                
                $unpackedFile = Join-Path $exe.DirectoryName "$($exe.Name).unpacked.exe"
                if (Test-Path -LiteralPath $unpackedFile) {
                    Copy-Item -LiteralPath $unpackedFile -Destination $exe.FullName -Force
                    Remove-Item -LiteralPath $unpackedFile -Force
                    Write-Host "  [SUCCESS] Unpacked $($exe.Name) - SteamStub DRM removed successfully!" -ForegroundColor Green
                } else {
                    Write-Host "  [NOTICE] Binary is not packed with SteamStub DRM or is a custom engine wrapper." -ForegroundColor Yellow
                    if (Test-Path -LiteralPath $backupPath) {
                        Remove-Item -LiteralPath $backupPath -Force
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

# Step 2: Handle steam_api64.dll according to selected mode
# ============================================================
Write-Host "[2/6] Processing steam_api64.dll instances..." -ForegroundColor Cyan

# Discover all steam_api64.dll or backup steam_api64_valve.dll locations
$pluginDirs = @()

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

if ($pluginDirs.Count -eq 0) { $pluginDirs += $ExeDir }

switch ($OnlineMode) {
    "valve" {
        # --- Mode 1: ReFix Online por Steam (Valve Mode) ---
        # Uses ReFix proxy steam_api64.dll forwarding to steam_api64_valve.dll + AppID spoofing.
        # Enables direct .exe launching with full Steam Overlay injection.
        Write-Host "  [Valve Mode] Deploying ReFix proxy steam_api64.dll with Steam Overlay injection..." -ForegroundColor Cyan
        $proxyPath = Join-Path $BinDir "steam_api64.dll"
        if (-not (Test-Path -LiteralPath $proxyPath)) {
            Write-Host "  [ERROR] ReFix proxy steam_api64.dll not found at: $proxyPath" -ForegroundColor Red
            exit 1
        }
        foreach ($dir in $pluginDirs) {
            $valvePath = Join-Path $dir "steam_api64_valve.dll"
            $steamPath = Join-Path $dir "steam_api64.dll"
            if (Test-Path -LiteralPath $steamPath) {
                $isAlreadyProxy = $false
                if (Test-Path -LiteralPath $proxyPath) {
                    if ((Get-Item $steamPath).Length -eq (Get-Item $proxyPath).Length) {
                        $isAlreadyProxy = $true
                    }
                }
                if (-not (Test-Path -LiteralPath $valvePath)) {
                    if (-not $isAlreadyProxy) {
                        Rename-Item -LiteralPath $steamPath -NewName "steam_api64_valve.dll" -Force
                        Write-Host "  [OK] Backed up original steam_api64.dll -> steam_api64_valve.dll in $dir" -ForegroundColor Green
                    } else {
                        Write-Host "  [NOTICE] Existing steam_api64.dll in $dir is already ReFix proxy (skipping backup to preserve original)" -ForegroundColor Yellow
                    }
                }
            }
            Copy-Item -LiteralPath $proxyPath -Destination $dir -Force
            Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
        }

        # Also deploy winmm.dll to ExeDir for early Steam Overlay injection (before Unity initializes DirectX)
        $winmmPath = Join-Path $BinDir "winmm.dll"
        if (Test-Path -LiteralPath $winmmPath) {
            $targetWinmm = Join-Path $ExeDir "winmm.dll"
            $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
            if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item $targetWinmm).Length -ne (Get-Item $winmmPath).Length)) {
                Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
            }
            Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force
            Write-Host "  [OK] Deployed ReFix winmm.dll proxy to root folder $ExeDir for early Steam Overlay injection" -ForegroundColor Green
        }

        # Synchronize ReFix.ini in ExeDir
        $reFixIniPath = Join-Path $ExeDir "ReFix.ini"
        $filterVal = if ($RealAppId -and $RealAppId -ne "0") { $RealAppId } else { "480" }
        $bypassVal = if ($DLCMode -eq "none") { "false" } else { "true" }
        $maskVal = if ($MaskAppId) { $MaskAppId } else { "480" }
        $realVal = if ($RealAppId -and $RealAppId -ne "0") { $RealAppId } else { "480" }
        $reFixIniContent = @"
; =============================================================================
; ReFix Universal Configuration File
; =============================================================================

[Game]
GameName=$GameName
EngineType=$EngineType

[Matchmaking]
EnableLobbyFilter=false
LobbyFilterKey=game_filter
LobbyFilterValue=$filterVal
LobbyDistanceFilter=Worldwide
MaxLobbyResults=50

[ServerBrowser]
OverrideServerListAppId=false
ServerListAppId=480
Language=english

[Steam]
MaskAppId=$maskVal
RealAppId=$realVal
Language=$Language
BypassLicenseCheck=$bypassVal
DLCs=$DLCs

[Overlay]
EnableOverlay=true
OverlayAppId=$maskVal

[EOS]
DeviceIdAuth=true

[User]
Name=$UserName
SteamId=

[Online]
Mode=valve

[Network]
GameFilter=$filterVal
PublicIP=
LocalIP=

[P2P]
EnableWAN=true
P2PPort=7777
AllowRelay=true
ForcePublicIPInLobby=true
"@
        [System.IO.File]::WriteAllText($reFixIniPath, $reFixIniContent)
        Write-Host "  [OK] Configured ReFix.ini in $ExeDir" -ForegroundColor Green
    }

    "photon" {
        # --- Photon Mode (Method 2) ---
        # Deploys ReFix proxy steam_api64.dll and BepInEx + NekogiriFix
        Write-Host "  [Photon Mode] Deploying ReFix proxy steam_api64.dll..." -ForegroundColor Cyan
        $proxyPath = Join-Path $BinDir "steam_api64.dll"
        if (-not (Test-Path -LiteralPath $proxyPath)) {
            Write-Host "  [ERROR] ReFix proxy steam_api64.dll not found at: $proxyPath" -ForegroundColor Red
            exit 1
        }
        foreach ($dir in $pluginDirs) {
            $valvePath = Join-Path $dir "steam_api64_valve.dll"
            $steamPath = Join-Path $dir "steam_api64.dll"
            if (Test-Path -LiteralPath $steamPath) {
                $isAlreadyProxy = $false
                if (Test-Path -LiteralPath $proxyPath) {
                    if ((Get-Item $steamPath).Length -eq (Get-Item $proxyPath).Length) {
                        $isAlreadyProxy = $true
                    }
                }
                if (-not (Test-Path -LiteralPath $valvePath)) {
                    if (-not $isAlreadyProxy) {
                        Rename-Item -LiteralPath $steamPath -NewName "steam_api64_valve.dll" -Force
                        Write-Host "  [OK] Backed up original steam_api64.dll -> steam_api64_valve.dll in $dir" -ForegroundColor Green
                    } else {
                        Write-Host "  [NOTICE] Existing steam_api64.dll in $dir is already ReFix proxy (skipping backup to preserve original)" -ForegroundColor Yellow
                    }
                }
            }
            Copy-Item -LiteralPath $proxyPath -Destination $dir -Force
            Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
        }
    }

    { $_ -in @("goldberg", "offline", "lan") } {
        # --- Mode 2: Re:Goldberg LAN sin Steam ---
        # Deploys Goldberg Steam Emulator backend with complete local settings synchronization,
        # persistent identity, LAN matchmaking discovery, and portable saves.
        Write-Host "  [Re:Goldberg LAN Mode] Deploying Goldberg Steam Emulator backend..." -ForegroundColor Cyan
        $goldbergDll = Join-Path $BinDir "goldberg\steam_api64.dll"
        if (-not (Test-Path -LiteralPath $goldbergDll)) {
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
            if (Test-Path -LiteralPath $valvePath) {
                $targetForInterfaces = $valvePath
            } elseif (Test-Path -LiteralPath $steamPath) {
                $targetForInterfaces = $steamPath
            }

            if ($targetForInterfaces -and (Test-Path -LiteralPath $genTool)) {
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
            
            # Always place Goldberg DLL as steam_api64_valve.dll
            Copy-Item -LiteralPath $goldbergDll -Destination $valvePath -Force
            Write-Host "  [OK] Deployed Goldberg emulator backend as steam_api64_valve.dll to $dir" -ForegroundColor Green

            # Deploy ReFix proxy as steam_api64.dll (provides SteamInternal_SteamAPI_Init and all API exports)
            Copy-Item -LiteralPath $proxyPath -Destination $steamPath -Force
            Write-Host "  [OK] Deployed ReFix proxy steam_api64.dll to $dir" -ForegroundColor Green
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
        if (Test-Path -LiteralPath $reFixIniPath) {
            $iniContent = Get-Content -LiteralPath $reFixIniPath -ErrorAction SilentlyContinue
            foreach ($line in $iniContent) {
                if ($line -match "^\s*RealAppId\s*=\s*([0-9]+)") { $finalRealAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $finalRealAppId -or $finalRealAppId -eq "0") { $finalRealAppId = "480" }

    $finalLanguage = $Language
    if (-not $finalLanguage) { $finalLanguage = "english" }

    $finalListenPort = $ListenPort
    if (-not $finalListenPort) { $finalListenPort = "47584" }

    # Create local saves folder in ExeDir for portable storage
    $savesDir = Join-Path $ExeDir "saves"
    if (-not (Test-Path -LiteralPath $savesDir)) {
        New-Item -ItemType Directory -Path $savesDir -Force | Out-Null
        Write-Host "  [OK] Initialized portable save directory: $savesDir" -ForegroundColor Green
    }

    # Populate steam_settings in ExeDir and all plugin directories
    $settingsDirsToPopulate = @($ExeDir)
    foreach ($pDir in $pluginDirs) {
        if ($settingsDirsToPopulate -notcontains $pDir) { $settingsDirsToPopulate += $pDir }
    }

    foreach ($baseDir in $settingsDirsToPopulate) {
        $settingsDir = Join-Path $baseDir "steam_settings"
        if (-not (Test-Path -LiteralPath $settingsDir)) {
            New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
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
        if ($generatedInterfacesFile -and (Test-Path -LiteralPath $generatedInterfacesFile)) {
            $destItf = Join-Path $settingsDir "steam_interfaces.txt"
            if ($generatedInterfacesFile -ne $destItf) {
                Copy-Item -LiteralPath $generatedInterfacesFile -Destination $destItf -Force
            }
        }

        # 5. Place steam_appid.txt and local_save.txt in baseDir
        [System.IO.File]::WriteAllText((Join-Path $baseDir "steam_appid.txt"), "$finalRealAppId`r`n")
        [System.IO.File]::WriteAllText((Join-Path $baseDir "local_save.txt"), "saves`r`n")

        Write-Host "  [OK] Synchronized steam_settings/ in $baseDir (User: $($identity.Name), SteamID: $($identity.SteamId), Port: $finalListenPort, DLC Mode: $effectiveDlcMode)" -ForegroundColor Green
    }

    # Synchronize and write unified ReFix.ini in ExeDir
    $reFixIniContent = @"
; =============================================================================
; ReFix Universal Configuration File
; =============================================================================

[Game]
GameName=$GameName
EngineType=$EngineType

[Online]
Mode=goldberg

[Steam]
MaskAppId=$MaskAppId
RealAppId=$finalRealAppId
Language=$finalLanguage
BypassLicenseCheck=$bypassLicenseVal
DLCs=$DLCs

[Matchmaking]
EnableLobbyFilter=false
LobbyFilterKey=game_filter
LobbyFilterValue=
LobbyDistanceFilter=worldwide
MaxLobbyResults=50

[ServerBrowser]
OverrideServerListAppId=false
ServerListAppId=480

[User]
Name=$($identity.Name)
AutoGenerateSteamId=$($identity.AutoGenerateSteamId)
SteamId=$($identity.SteamId)

[Network]
ListenPort=$finalListenPort
CustomBroadcasts=$CustomBroadcasts

[Storage]
LocalSave=saves

[Debug]
EnableLog=true
EnableConsole=false

[EOS]
DeviceIdAuth=true
"@
    [System.IO.File]::WriteAllText($reFixIniPath, $reFixIniContent)
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
    if (Test-Path -LiteralPath $reFixIni) {
        $iniLines = Get-Content -LiteralPath $reFixIni -ErrorAction SilentlyContinue
        foreach ($line in $iniLines) {
            if ($OnlineMode -eq "valve") {
                if ($line -match "^MaskAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^RealAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
                if ($targetAppId -eq "0" -or -not $targetAppId) {
                    if ($line -match "^MaskAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
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

    # For Mono games, apply Mono.Cecil patches across Managed assemblies
    $managedDir = Join-Path $TargetDir "*_Data\Managed"
    $managedFolders = Get-Item -LiteralPath $managedDir -ErrorAction SilentlyContinue

    $cecilPath = Join-Path $BinDir "bepinex\core\Mono.Cecil.dll"
    if (-not (Test-Path -LiteralPath $cecilPath)) {
        $cecilItem = Get-ChildItem -LiteralPath $BinDir -Filter "Mono.Cecil.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cecilItem) { $cecilPath = $cecilItem.FullName }
    }

    if (Test-Path -LiteralPath $cecilPath) {
        try { Add-Type -Path $cecilPath -ErrorAction SilentlyContinue } catch {}
    }

    foreach ($mFolder in $managedFolders) {
        $candidateDlls = Get-ChildItem -LiteralPath $mFolder.FullName -Filter "*.dll" -ErrorAction SilentlyContinue | 
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

                if ($nativeType -and $apiType) {
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
                    if (-not (Test-Path -LiteralPath $origPath)) {
                        Copy-Item -LiteralPath $dllItem.FullName -Destination $origPath -Force
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
        if (-not (Test-Path -LiteralPath $bepinexSrc)) {
            Write-Host "  [ERROR] BepInEx source not found at: $bepinexSrc" -ForegroundColor Red
            exit 1
        }

        # Deploy winhttp.dll (doorstop proxy) to game root
        $winhttpSrc = Join-Path $bepinexSrc "winhttp.dll"
        if (Test-Path -LiteralPath $winhttpSrc) {
            Copy-Item -LiteralPath $winhttpSrc -Destination $TargetDir -Force
            Write-Host "  [OK] Deployed winhttp.dll (BepInEx doorstop) to game root" -ForegroundColor Green
        }

        # Deploy doorstop_config.ini to game root
        $doorstopSrc = Join-Path $bepinexSrc "doorstop_config.ini"
        if (Test-Path -LiteralPath $doorstopSrc) {
            Copy-Item -LiteralPath $doorstopSrc -Destination $TargetDir -Force
            Write-Host "  [OK] Deployed doorstop_config.ini to game root" -ForegroundColor Green
        }

        # Deploy BepInEx/core/ directory
        $coreSrc = Join-Path $bepinexSrc "core"
        $coreDst = Join-Path $TargetDir "BepInEx\core"
        if (-not (Test-Path -LiteralPath $coreDst)) { New-Item -ItemType Directory -Path $coreDst -Force | Out-Null }
        Copy-Item -LiteralPath "$coreSrc\*" -Destination $coreDst -Recurse -Force
        Write-Host "  [OK] Deployed BepInEx\core\ ($(Get-ChildItem $coreDst -File | Measure-Object | Select-Object -Exp Count) files)" -ForegroundColor Green

        # Deploy UniversalPhotonFix plugin
        $pluginsSrc = Join-Path $bepinexSrc "plugins"
        $pluginsDst = Join-Path $TargetDir "BepInEx\plugins"
        if (-not (Test-Path -LiteralPath $pluginsDst)) { New-Item -ItemType Directory -Path $pluginsDst -Force | Out-Null }
        $pluginSrc = Join-Path $pluginsSrc "UniversalPhotonFix.dll"
        if (Test-Path -LiteralPath $pluginSrc) {
            Copy-Item -LiteralPath $pluginSrc -Destination $pluginsDst -Force
            Write-Host "  [OK] Deployed UniversalPhotonFix.dll plugin" -ForegroundColor Green
        } else {
            $nekogiriSrc = Join-Path $pluginsSrc "NekogiriFix.dll"
            if (Test-Path -LiteralPath $nekogiriSrc) {
                Copy-Item -LiteralPath $nekogiriSrc -Destination $pluginsDst -Force
                Write-Host "  [OK] Deployed NekogiriFix.dll plugin" -ForegroundColor Green
            } else {
                Write-Host "  [WARNING] UniversalPhotonFix.dll not found in $pluginsSrc" -ForegroundColor Yellow
            }
        }

        # Create BepInEx\config directory
        $configDst = Join-Path $TargetDir "BepInEx\config"
        if (-not (Test-Path -LiteralPath $configDst)) { New-Item -ItemType Directory -Path $configDst -Force | Out-Null }

        # Generate Kirigiri.ini from ReFix.ini Photon settings
        Write-Host "  [Photon] Generating Kirigiri.ini from Photon configuration..." -ForegroundColor Cyan

        $reFixIni = Join-Path $ExeDir "ReFix.ini"
        $steamAppIdValue = "480"
        if (Test-Path -LiteralPath $reFixIni) {
            $iniContent = Get-Content -LiteralPath $reFixIni -ErrorAction SilentlyContinue
            foreach ($line in $iniContent) {
                if ($line -match "^MaskAppId=(.+)") { $steamAppIdValue = $Matches[1].Trim() }
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
; To change Photon settings, edit ReFix.ini [Online] section
; and re-run AutoDeploy, or edit this file directly.
;
; Get your free Photon AppID at:
;   https://dashboard.photonengine.com
;   Dashboard > Create App > Select "Photon Realtime" (NOT PUN2 or Fusion)
; ============================================================

[Settings]
SteamAppId=$steamAppIdValue
AppIdRealtime=$PhotonAppId
AppIdVoice=
AppIdFusion=
Auth=None
FixedRegion=$PhotonRegion
"@
        [System.IO.File]::WriteAllText($kirigiriPath, $kirigiriContent)
        Write-Host "  [OK] Generated Kirigiri.ini (AppIdRealtime=$PhotonAppId, FixedRegion=$PhotonRegion)" -ForegroundColor Green

        if (-not $PhotonAppId) {
            Write-Host "  [REMINDER] PhotonAppIdRealtime is empty!" -ForegroundColor Yellow
            Write-Host "  [REMINDER] Edit Kirigiri.ini or ReFix.ini [Online] section before launching the game." -ForegroundColor Yellow
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
                if ($line -match "^MaskAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^RealAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }

    # Place steam_appid.txt in Godot root folder
    $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
    [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
    Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in Godot root $ExeDir" -ForegroundColor Green

    $winmmPath = Join-Path $BinDir "winmm.dll"
    if (Test-Path -LiteralPath $winmmPath) {
        $targetWinmm = Join-Path $ExeDir "winmm.dll"
        $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
        if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item $targetWinmm).Length -ne (Get-Item $winmmPath).Length)) {
            Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
        }
        Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force
        Write-Host "  [OK] Deployed ReFix winmm.dll proxy to Godot root folder $ExeDir" -ForegroundColor Green
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
                if ($line -match "^MaskAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
            } else {
                if ($line -match "^RealAppId=(.+)") { $targetAppId = $Matches[1].Trim() }
            }
        }
    }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }

    # 1. Place steam_appid.txt in ExeDir and TargetDir root
    $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
    [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
    Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $ExeDir" -ForegroundColor Green

    $rootAppIdPath = Join-Path $TargetDir "steam_appid.txt"
    [System.IO.File]::WriteAllText($rootAppIdPath, "$targetAppId`r`n")

    # 2. Deploy EOSSDK-Win64-Shipping.dll / RedboneEOS.dll proxy if game uses EOS / Redpoint
    $eosProxyPath = Join-Path $BinDir "EOSSDK-Win64-Shipping.dll"
    $redboneProxyPath = Join-Path $BinDir "RedboneEOS.dll"
    if (-not (Test-Path -LiteralPath $redboneProxyPath) -and (Test-Path -LiteralPath $eosProxyPath)) {
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
    
    # Always ensure ExeDir is in eosDirs if EOS is present so the proxy is placed beside the shipping executable
    if ($eosDirs.Count -gt 0 -and ($eosDirs -notcontains $ExeDir)) {
        $eosDirs += $ExeDir
    }

    if ($eosDirs.Count -gt 0 -and (Test-Path -LiteralPath $eosProxyPath)) {
        $proxySize = (Get-Item $eosProxyPath).Length
        foreach ($dir in $eosDirs) {
            $eosOriginal = Join-Path $dir "EOSSDK_original.dll"
            $eosPath = Join-Path $dir "EOSSDK-Win64-Shipping.dll"

            if (Test-Path -LiteralPath $eosPath) {
                $curSize = (Get-Item $eosPath).Length
                if ($curSize -ne $proxySize -and (-not (Test-Path -LiteralPath $eosOriginal))) {
                    Rename-Item -LiteralPath $eosPath -NewName "EOSSDK_original.dll" -Force
                    Write-Host "  [OK] Renamed original EOSSDK -> EOSSDK_original.dll in $dir" -ForegroundColor Green
                }
            }
            Copy-Item -LiteralPath $eosProxyPath -Destination $dir -Force
            Write-Host "  [OK] Deployed EOSSDK-Win64-Shipping.dll proxy to $dir" -ForegroundColor Green

            # If RedboneEOS.dll exists, replace with proxy
            $targetRedbone = Join-Path $dir "RedboneEOS.dll"
            if (Test-Path -LiteralPath $targetRedbone) {
                Copy-Item -LiteralPath $redboneProxyPath -Destination $targetRedbone -Force
                Write-Host "  [OK] Deployed RedboneEOS.dll proxy to $dir" -ForegroundColor Green
            }
        }
    }

    # 3. Deploy winmm.dll proxy to Unreal root folder ExeDir
    $winmmPath = Join-Path $BinDir "winmm.dll"
    if (Test-Path -LiteralPath $winmmPath) {
        $targetWinmm = Join-Path $ExeDir "winmm.dll"
        $targetWinmmOrig = Join-Path $ExeDir "winmm_o.dll"
        if ((Test-Path -LiteralPath $targetWinmm) -and (-not (Test-Path -LiteralPath $targetWinmmOrig)) -and ((Get-Item $targetWinmm).Length -ne (Get-Item $winmmPath).Length)) {
            Rename-Item -LiteralPath $targetWinmm -NewName "winmm_o.dll" -Force
        }
        Copy-Item -LiteralPath $winmmPath -Destination $targetWinmm -Force
        Write-Host "  [OK] Deployed ReFix winmm.dll proxy to Unreal root folder $ExeDir" -ForegroundColor Green
    }
} else {
    Write-Host "[4/6] Processing Native/Custom deployment (mode: $OnlineMode)..." -ForegroundColor Cyan
    $exeAppIdPath = Join-Path $ExeDir "steam_appid.txt"
    $targetAppId = if ($OnlineMode -eq "valve") { $MaskAppId } else { $finalRealAppId }
    if (-not $targetAppId -or $targetAppId -eq "0") { $targetAppId = "480" }
    [System.IO.File]::WriteAllText($exeAppIdPath, "$targetAppId`r`n")
    Write-Host "  [OK] Placed steam_appid.txt ($targetAppId) in $ExeDir" -ForegroundColor Green

    $rootAppIdPath = Join-Path $TargetDir "steam_appid.txt"
    if ($rootAppIdPath -ne $exeAppIdPath) {
        [System.IO.File]::WriteAllText($rootAppIdPath, "$targetAppId`r`n")
    }
}

