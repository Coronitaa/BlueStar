param(
    [Parameter(Mandatory=$true)]
    [string]$TargetDir,

    [string]$BinDir = "",
    [string]$Action = "install",   # "install" | "uninstall" | "status"
    [string]$AppId = "",
    [string]$GameName = "",
    [string]$DLCMode = "all",       # "all" | "none" | "custom"
    [string]$DLCs = ""              # Predefined DLC list (comma-separated AppIDs or AppID=Name pairs)
)

# Enable TLS 1.2
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$target = $TargetDir.TrimEnd('\').Trim('"')
if (-not (Test-Path $target)) {
    Write-Host "[ERROR] Target directory does not exist: $target" -ForegroundColor Red
    exit 1
}

if (-not $BinDir) {
    $BinDir = Join-Path $PSScriptRoot "..\bin"
}
$smokeToolsDir = Join-Path $BinDir "tools\SmokeAPI"
if (-not (Test-Path $smokeToolsDir)) {
    $smokeToolsDir = Join-Path $PSScriptRoot "tools\SmokeAPI"
}

$smoke64 = Join-Path $smokeToolsDir "smoke_api64.dll"
$smoke32 = Join-Path $smokeToolsDir "smoke_api32.dll"

# --- 1. Locate all directories with steam_api*.dll ---
$targetDlls = Get-ChildItem -Path $target -Filter "steam_api*.dll" -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "*_o.dll*" -and $_.Name -notlike "*_valve.dll*" -and $_.Name -notlike "*_goldberg.dll*" }

$targetDirs = @()
foreach ($dll in $targetDlls) {
    if ($targetDirs -notcontains $dll.DirectoryName) {
        $targetDirs += $dll.DirectoryName
    }
}

if ($targetDirs.Count -eq 0) {
    $targetDirs += $target
}

# --- Action: STATUS ---
if ($Action -eq "status") {
    Write-Host "`n=== DLC Unlocker Status for $target ===" -ForegroundColor Cyan
    foreach ($dir in $targetDirs) {
        $has64Bak = Test-Path (Join-Path $dir "steam_api64_o.dll")
        $has32Bak = Test-Path (Join-Path $dir "steam_api_o.dll")
        $hasCreamIni = Test-Path (Join-Path $dir "cream_api.ini")
        $hasSmokeJson = Test-Path (Join-Path $dir "SmokeAPI.config.json")
        $isInstalled = ($has64Bak -or $has32Bak) -and ($hasCreamIni -or $hasSmokeJson)
        
        $unlockMode = "Unknown"
        if ($hasCreamIni) {
            $iniContent = Get-Content (Join-Path $dir "cream_api.ini") -ErrorAction SilentlyContinue
            if ($iniContent -match "unlockall\s*=\s*true") { $unlockMode = "Unlock All (unlock_all=true)" }
            elseif ($iniContent -match "unlockall\s*=\s*false") { $unlockMode = "Specific Unlock or Disabled (unlock_all=false)" }
        }

        Write-Host "Directory: $dir" -ForegroundColor White
        Write-Host "  - Installed: $isInstalled" -ForegroundColor $(if ($isInstalled) { "Green" } else { "DarkGray" })
        Write-Host "  - Configured Mode: $unlockMode" -ForegroundColor Yellow
        Write-Host "  - Backup x64: $has64Bak | Backup x86: $has32Bak" -ForegroundColor DarkGray
        Write-Host "  - cream_api.ini: $hasCreamIni | SmokeAPI.config.json: $hasSmokeJson" -ForegroundColor DarkGray
    }
    exit 0
}

# --- Action: UNINSTALL ---
if ($Action -eq "uninstall") {
    Write-Host "`n=== Uninstalling DLC Unlocker in: $target ===" -ForegroundColor Yellow
    $restoredCount = 0
    foreach ($dir in $targetDirs) {
        $bak64 = Join-Path $dir "steam_api64_o.dll"
        $api64 = Join-Path $dir "steam_api64.dll"
        if (Test-Path $bak64) {
            if (Test-Path $api64) { Remove-Item -Path $api64 -Force }
            Move-Item -Path $bak64 -Destination $api64 -Force
            Write-Host "  [OK] Restored original steam_api64.dll in $dir" -ForegroundColor Green
            $restoredCount++
        }

        $bak32 = Join-Path $dir "steam_api_o.dll"
        $api32 = Join-Path $dir "steam_api.dll"
        if (Test-Path $bak32) {
            if (Test-Path $api32) { Remove-Item -Path $api32 -Force }
            Move-Item -Path $bak32 -Destination $api32 -Force
            Write-Host "  [OK] Restored original steam_api.dll in $dir" -ForegroundColor Green
            $restoredCount++
        }

        foreach ($cfg in @("cream_api.ini", "SmokeAPI.config.json", "SmokeAPI.json", "SmokeAPI.log", "SmokeAPI.cache.json")) {
            $cfgPath = Join-Path $dir $cfg
            if (Test-Path $cfgPath) {
                Remove-Item -Path $cfgPath -Force -ErrorAction SilentlyContinue
                Write-Host "  [OK] Removed configuration file: $cfg" -ForegroundColor Green
            }
        }
    }

    if ($restoredCount -gt 0) {
        Write-Host "`n[SUCCESS] DLC Unlocker uninstalled and original Steam DLLs restored." -ForegroundColor Green
    } else {
        Write-Host "`n[NOTICE] No DLC Unlocker backups found to restore." -ForegroundColor Yellow
    }
    exit 0
}

# --- Action: INSTALL ---
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " [ReFix DLC Unlocker] Target: $target" -ForegroundColor Cyan
Write-Host " [ReFix DLC Unlocker] Engine: BLUESTAR SmokeAPI Backend" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Resolve AppId if missing
$finalAppId = $AppId
if (-not $finalAppId -or $finalAppId -eq "0" -or $finalAppId -eq "480") {
    $appIdFile = Get-ChildItem -Path $target -Filter "steam_appid.txt" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($appIdFile) {
        $content = Get-Content $appIdFile.FullName -Raw -ErrorAction SilentlyContinue
        if ($content -match "([0-9]{3,9})") { $finalAppId = $Matches[1].Trim() }
    }
}
if (-not $finalAppId -or $finalAppId -eq "0") {
    $finalAppId = "480"
}

$dlcList = @{}
$unlockAll = $true
$effectiveMode = $DLCMode.ToLower()

# 2. Process DLC selection mode
if ($effectiveMode -eq "none" -or $DLCs -eq "none") {
    Write-Host "[1/3] Selected Mode: NONE (Lock all DLCs)..." -ForegroundColor Yellow
    $unlockAll = $false
    $dlcList = @{}
} elseif ($effectiveMode -eq "custom" -or ($DLCs -and $DLCs -ne "all" -and $DLCs -ne "none")) {
    $unlockAll = $false
    
    # If DLCs parameter is already passed
    if ($DLCs -and $DLCs -ne "custom" -and $DLCs -ne "all") {
        Write-Host "[1/3] Processing provided custom DLC list..." -ForegroundColor Cyan
        $items = $DLCs -split '[,;]'
        foreach ($it in $items) {
            $trimIt = $it.Trim()
            if (-not $trimIt) { continue }
            if ($trimIt -match "^(\d+)=(.+)$") {
                $dlcList[$Matches[1].Trim()] = $Matches[2].Trim()
            } elseif ($trimIt -match "^\d+$") {
                $dlcList[$trimIt] = "DLC $trimIt"
            }
        }
    } else {
        # Interactive selection
        $selectScript = Join-Path $BinDir "select_dlcs.ps1"
        if (-not (Test-Path $selectScript)) {
            $selectScript = Join-Path $PSScriptRoot "select_dlcs.ps1"
        }

        if (Test-Path $selectScript) {
            $tmpOut = Join-Path $env:TEMP ("refix_dlc_sel_" + (Get-Random) + ".txt")
            & powershell -NoProfile -ExecutionPolicy Bypass -File $selectScript -AppId $finalAppId -GameName $GameName -OutputFile $tmpOut
            
            if (Test-Path $tmpOut) {
                $lines = Get-Content $tmpOut -ErrorAction SilentlyContinue
                if ($lines.Count -gt 0) {
                    $resMode = $lines[0].Trim().ToLower()
                    if ($resMode -eq "none") {
                        $unlockAll = $false
                        $dlcList = @{}
                    } elseif ($resMode -eq "all") {
                        $unlockAll = $true
                        if ($lines.Count -gt 2) {
                            for ($i = 2; $i -lt $lines.Count; $i++) {
                                if ($lines[$i] -match "^(\d+)=(.+)$") {
                                    $dlcList[$Matches[1].Trim()] = $Matches[2].Trim()
                                }
                            }
                        }
                    } else {
                        $unlockAll = $false
                        if ($lines.Count -gt 2) {
                            for ($i = 2; $i -lt $lines.Count; $i++) {
                                if ($lines[$i] -match "^(\d+)=(.+)$") {
                                    $dlcList[$Matches[1].Trim()] = $Matches[2].Trim()
                                }
                            }
                        } elseif ($lines.Count -gt 1 -and $lines[1].Trim()) {
                            foreach ($id in ($lines[1] -split ',')) {
                                if ($id.Trim()) { $dlcList[$id.Trim()] = "DLC $($id.Trim())" }
                            }
                        }
                    }
                }
                Remove-Item $tmpOut -Force -ErrorAction SilentlyContinue
            }
        }
    }
} else {
    # Mode "all" (Default)
    Write-Host "[1/3] Selected Mode: ALL (Universal DLC Unlock)..." -ForegroundColor Cyan
    $unlockAll = $true

    if ($finalAppId -and $finalAppId -ne "480") {
        Write-Host "  [INFO] Querying official catalog on Steam Store API (AppID: $finalAppId)..." -ForegroundColor DarkGray
        try {
            Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
            $client = [System.Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds(5)
            $apiUrl = "https://store.steampowered.com/api/appdetails?appids=$finalAppId"
            $response = $client.GetStringAsync($apiUrl).GetAwaiter().GetResult()
            $json = ConvertFrom-Json $response
            $appData = $json.$finalAppId.data
            if ($appData -and $appData.dlc) {
                $dlcIds = $appData.dlc
                Write-Host "  [OK] Found $($dlcIds.Count) official DLC(s) for AppID $finalAppId." -ForegroundColor Green
                foreach ($dId in $dlcIds) {
                    $dlcList["$dId"] = "DLC $dId"
                }
            }
        } catch {
            Write-Host "  [NOTICE] Could not query Steam Store API (Offline mode or connection error). Universal unlock_all will be applied." -ForegroundColor Yellow
        }
    }
}

# 3. Deploy SmokeAPI and generate configs in all relevant directories
Write-Host "[2/3] Deploying SmokeAPI binaries and creating backups..." -ForegroundColor Cyan
$deployedCount = 0

foreach ($dir in $targetDirs) {
    $api64 = Join-Path $dir "steam_api64.dll"
    $bak64 = Join-Path $dir "steam_api64_o.dll"

    if (Test-Path $api64) {
        if (-not (Test-Path $bak64)) {
            Copy-Item -Path $api64 -Destination $bak64 -Force
            Write-Host "  [OK] Created backup: steam_api64.dll -> steam_api64_o.dll in $dir" -ForegroundColor Green
        }
    }
    if (Test-Path $smoke64) {
        Copy-Item -Path $smoke64 -Destination $api64 -Force
        Write-Host "  [OK] Deployed SmokeAPI 64-bit -> $api64" -ForegroundColor Green
        $deployedCount++
    }

    $api32 = Join-Path $dir "steam_api.dll"
    $bak32 = Join-Path $dir "steam_api_o.dll"
    if (Test-Path $api32) {
        if (-not (Test-Path $bak32)) {
            Copy-Item -Path $api32 -Destination $bak32 -Force
            Write-Host "  [OK] Created backup: steam_api.dll -> steam_api_o.dll in $dir" -ForegroundColor Green
        }
        if (Test-Path $smoke32) {
            Copy-Item -Path $smoke32 -Destination $api32 -Force
            Write-Host "  [OK] Deployed SmokeAPI 32-bit -> $api32" -ForegroundColor Green
            $deployedCount++
        }
    }

    # 4. Generate cream_api.ini (BLUESTAR standard)
    Write-Host "[3/3] Generating cream_api.ini and SmokeAPI.config.json..." -ForegroundColor Cyan
    $unlockAllStr = if ($unlockAll) { "true" } else { "false" }
    
    $creamIniLines = @(
        "; =============================================================================",
        "; Generated by ReFix DLC Unlocker (BLUESTAR Engine)",
        "; =============================================================================",
        "[steam]",
        "appid = $finalAppId",
        "unlockall = $unlockAllStr",
        "orgapi = steam_api_o.dll",
        "orgapi64 = steam_api64_o.dll",
        "extraprotection = false",
        "forceoffline = false",
        "",
        "[steam_misc]",
        "disableuserinterface = false",
        "",
        "[dlc]"
    )
    foreach ($k in $dlcList.Keys) {
        $creamIniLines += "$k = $($dlcList[$k])"
    }
    $creamIniPath = Join-Path $dir "cream_api.ini"
    [System.IO.File]::WriteAllText($creamIniPath, ($creamIniLines -join "`r`n") + "`r`n", [System.Text.Encoding]::UTF8)
    Write-Host "  [OK] Generated $creamIniPath (unlockall = $unlockAllStr)" -ForegroundColor Green

    # 5. Generate SmokeAPI.config.json
    $smokeJsonPath = Join-Path $dir "SmokeAPI.config.json"
    $smokeConfig = @{
        logging = $false
        unlock_all = $unlockAll
        dlcs = $dlcList
    }
    $jsonStr = ConvertTo-Json $smokeConfig -Depth 5
    [System.IO.File]::WriteAllText($smokeJsonPath, $jsonStr + "`r`n", [System.Text.Encoding]::UTF8)
    Write-Host "  [OK] Generated $smokeJsonPath (unlock_all = $unlockAllStr)" -ForegroundColor Green
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " DLC UNLOCKER INSTALLED SUCCESSFULLY" -ForegroundColor Green
Write-Host " Game: $GameName  |  AppID: $finalAppId" -ForegroundColor Green
if ($unlockAll) {
    Write-Host " Mode: Universal Unlock of ALL DLCs (unlock_all = true)" -ForegroundColor Green
} elseif ($dlcList.Count -gt 0) {
    Write-Host " Mode: Specific Unlock of $($dlcList.Count) DLC(s) (unlock_all = false)" -ForegroundColor Green
    foreach ($k in $dlcList.Keys) {
        Write-Host "   - ${k}: $($dlcList[$k])" -ForegroundColor White
    }
} else {
    Write-Host " Mode: NO DLCs Unlocked (unlock_all = false, empty list)" -ForegroundColor Yellow
}
Write-Host "============================================================" -ForegroundColor Green
