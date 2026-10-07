param(
    [string]$AppId = "480",
    [string]$GameName = "",
    [string]$OutputFile = "",
    [string]$PreselectedMode = ""   # "all" | "none" | "custom"
)

# Enable TLS 1.2
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$finalAppId = $AppId.Trim()
if (-not $finalAppId -or $finalAppId -eq "0") { $finalAppId = "480" }

$dlcItems = @()

# Fetch official DLC catalog if AppId is valid and not Spacewar (480)
if ($finalAppId -ne "480") {
    Write-Host "`n[INFO] Querying DLC catalog on Steam Store API (AppID: $finalAppId)..." -ForegroundColor Cyan
    try {
        Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(6)
        
        $url = "https://store.steampowered.com/api/appdetails?appids=$finalAppId"
        $raw = $client.GetStringAsync($url).GetAwaiter().GetResult()
        $json = ConvertFrom-Json $raw
        $appData = $json.$finalAppId.data

        if ($appData) {
            if (-not $GameName -and $appData.name) { $GameName = $appData.name }
            if ($appData.dlc -and $appData.dlc.Count -gt 0) {
                $dlcIds = $appData.dlc
                Write-Host "  [OK] Found $($dlcIds.Count) official DLC(s) for $GameName." -ForegroundColor Green
                Write-Host "  [INFO] Retrieving DLC names..." -ForegroundColor DarkGray

                # Async fetch names for all DLCs
                $tasks = @()
                foreach ($d in $dlcIds) {
                    $dUrl = "https://store.steampowered.com/api/appdetails?appids=$d&filters=basic"
                    $tasks += @{ Id = "$d"; Task = $client.GetStringAsync($dUrl) }
                }

                foreach ($t in $tasks) {
                    $dName = "DLC $($t.Id)"
                    try {
                        $resText = $t.Task.GetAwaiter().GetResult()
                        $dJson = ConvertFrom-Json $resText
                        $dId = $t.Id
                        if ($dJson.$dId.data.name) {
                            $dName = $dJson.$dId.data.name
                        }
                    } catch {}
                    $dlcItems += [PSCustomObject]@{
                        Id = "$($t.Id)"
                        Name = "$dName"
                    }
                }
            }
        }
    } catch {
        Write-Host "  [NOTICE] Could not connect to Steam Store API (Offline mode or connection error)." -ForegroundColor Yellow
    }
}

$selectedIds = @()
$selectedLines = @()
$selectionMode = "all"

if ($PreselectedMode -eq "all") {
    $selectionMode = "all"
    if ($dlcItems.Count -gt 0) {
        foreach ($item in $dlcItems) {
            $selectedIds += $item.Id
            $selectedLines += "$($item.Id)=$($item.Name)"
        }
    } else {
        $selectedIds = @("all")
    }
} elseif ($PreselectedMode -eq "none") {
    $selectionMode = "none"
    $selectedIds = @()
    $selectedLines = @()
} else {
    # Interactive selection mode
    if ($dlcItems.Count -gt 0) {
        Write-Host "`n============================================================" -ForegroundColor Cyan
        Write-Host " Official Steam DLC Catalog" -ForegroundColor Cyan
        if ($GameName) { Write-Host " Game: $GameName (AppID: $finalAppId)" -ForegroundColor Cyan }
        Write-Host "============================================================" -ForegroundColor Cyan
        Write-Host ""

        for ($i = 0; $i -lt $dlcItems.Count; $i++) {
            $num = ($i + 1).ToString().PadLeft(2)
            Write-Host " [$num] $($dlcItems[$i].Id) - $($dlcItems[$i].Name)" -ForegroundColor White
        }

        Write-Host ""
        Write-Host " Selection Options:" -ForegroundColor Yellow
        Write-Host "  - Comma-separated numbers or ranges (e.g. 1,3,4 or 1-3)" -ForegroundColor DarkGray
        Write-Host "  - Direct AppIDs separated by comma (e.g. $($dlcItems[0].Id), $($dlcItems[-1].Id))" -ForegroundColor DarkGray
        Write-Host "  - 'all'  -> Unlock ALL $($dlcItems.Count) DLCs" -ForegroundColor DarkGray
        Write-Host "  - 'none' -> Unlock NO DLCs" -ForegroundColor DarkGray
        Write-Host ""

        $userInput = Read-Host "Enter your DLC selection [Default: all]"
        $userInput = $userInput.Trim()

        if (-not $userInput -or $userInput.ToLower() -eq "all" -or $userInput -eq "*") {
            $selectionMode = "all"
            foreach ($item in $dlcItems) {
                $selectedIds += $item.Id
                $selectedLines += "$($item.Id)=$($item.Name)"
            }
        } elseif ($userInput.ToLower() -eq "none" -or $userInput -eq "0") {
            $selectionMode = "none"
            $selectedIds = @()
            $selectedLines = @()
        } else {
            $selectionMode = "custom"
            $tokens = $userInput -split '[,; ]+'
            foreach ($token in $tokens) {
                $t = $token.Trim()
                if (-not $t) { continue }

                # Range like 1-3
                if ($t -match "^(\d+)\s*-\s*(\d+)$") {
                    $start = [int]$Matches[1]
                    $end = [int]$Matches[2]
                    if ($start -gt $end) { $tmp = $start; $start = $end; $end = $tmp }
                    for ($idx = $start; $idx -le $end; $idx++) {
                        if ($idx -ge 1 -and $idx -le $dlcItems.Count) {
                            $targetItem = $dlcItems[$idx - 1]
                            if ($selectedIds -notcontains $targetItem.Id) {
                                $selectedIds += $targetItem.Id
                                $selectedLines += "$($targetItem.Id)=$($targetItem.Name)"
                            }
                        }
                    }
                }
                # Index number like 1, 2, 3
                elseif ($t -match "^\d+$" -and [int]$t -ge 1 -and [int]$t -le $dlcItems.Count) {
                    $idx = [int]$t
                    $targetItem = $dlcItems[$idx - 1]
                    if ($selectedIds -notcontains $targetItem.Id) {
                        $selectedIds += $targetItem.Id
                        $selectedLines += "$($targetItem.Id)=$($targetItem.Name)"
                    }
                }
                # Raw AppID (e.g. 1149640)
                elseif ($t -match "^\d{4,10}$") {
                    $matchedItem = $dlcItems | Where-Object { $_.Id -eq $t } | Select-Object -First 1
                    $name = if ($matchedItem) { $matchedItem.Name } else { "DLC $t" }
                    if ($selectedIds -notcontains $t) {
                        $selectedIds += $t
                        $selectedLines += "$t=$name"
                    }
                }
            }

            if ($selectedIds.Count -eq 0) {
                Write-Host "  [NOTICE] No valid indices recognized. Applying 'all' by default." -ForegroundColor Yellow
                $selectionMode = "all"
                foreach ($item in $dlcItems) {
                    $selectedIds += $item.Id
                    $selectedLines += "$($item.Id)=$($item.Name)"
                }
            }
        }
    } else {
        Write-Host "`n============================================================" -ForegroundColor Cyan
        Write-Host " Manual DLC Entry" -ForegroundColor Cyan
        Write-Host "============================================================" -ForegroundColor Cyan
        Write-Host " Enter DLC AppIDs separated by comma (e.g. 12345,67890)," -ForegroundColor DarkGray
        Write-Host " or type 'all' for universal unlock, or 'none' for none." -ForegroundColor DarkGray
        Write-Host ""
        $manualInput = Read-Host "Enter DLC AppIDs [Default: all]"
        $manualInput = $manualInput.Trim()

        if (-not $manualInput -or $manualInput.ToLower() -eq "all" -or $manualInput -eq "*") {
            $selectionMode = "all"
            $selectedIds = @("all")
            $selectedLines = @()
        } elseif ($manualInput.ToLower() -eq "none" -or $manualInput -eq "0") {
            $selectionMode = "none"
            $selectedIds = @()
            $selectedLines = @()
        } else {
            $selectionMode = "custom"
            $tokens = $manualInput -split '[,; ]+'
            foreach ($token in $tokens) {
                $t = $token.Trim()
                if ($t -match "^\d+$") {
                    if ($selectedIds -notcontains $t) {
                        $selectedIds += $t
                        $selectedLines += "$t=DLC $t"
                    }
                }
            }
        }
    }
}

Write-Host "`n[SELECTION SUMMARY]" -ForegroundColor Green
Write-Host "  - Mode: $selectionMode" -ForegroundColor White
if ($selectionMode -eq "none") {
    Write-Host "  - DLCs to unlock: NONE (Locked completely)" -ForegroundColor Yellow
} elseif ($selectionMode -eq "all") {
    Write-Host "  - DLCs to unlock: ALL ($($selectedIds.Count) selected)" -ForegroundColor Green
} else {
    Write-Host "  - Selected DLCs ($($selectedIds.Count)): $($selectedIds -join ', ')" -ForegroundColor Green
}

if ($OutputFile) {
    # Line 1: Mode (all | none | custom)
    # Line 2: Comma-separated AppIDs
    # Line 3+: AppID=Name pairs
    $fileContent = @($selectionMode, ($selectedIds -join ',')) + $selectedLines
    [System.IO.File]::WriteAllText($OutputFile, ($fileContent -join "`r`n") + "`r`n", [System.Text.Encoding]::UTF8)
}

return ($selectedIds -join ',')
