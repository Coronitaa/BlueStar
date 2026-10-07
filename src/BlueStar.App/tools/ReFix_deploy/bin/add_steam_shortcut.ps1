param(
    [string]$GameName,
    [string]$ExePath
)

function Get-SteamPath {
    try {
        $reg = Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction Stop
        if ($reg.SteamPath -and (Test-Path $reg.SteamPath)) { return $reg.SteamPath }
    } catch {}
    try {
        $regLM = Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction Stop
        if ($regLM.InstallPath -and (Test-Path $regLM.InstallPath)) { return $regLM.InstallPath }
    } catch {}
    try {
        $regLM64 = Get-ItemProperty -Path "HKLM:\SOFTWARE\Valve\Steam" -ErrorAction Stop
        if ($regLM64.InstallPath -and (Test-Path $regLM64.InstallPath)) { return $regLM64.InstallPath }
    } catch {}
    $common = @(
        "${env:ProgramFiles(x86)}\Steam",
        "${env:ProgramFiles}\Steam",
        "C:\Steam", "D:\Steam", "E:\Steam", "F:\Steam", "G:\Steam"
    )
    foreach ($p in $common) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    return $null
}

function Get-Crc32([string]$str) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($str)
    $crc = 0xffffffff
    foreach ($b in $bytes) {
        $crc = $crc -bxor $b
        for ($i = 0; $i -lt 8; $i++) {
            if (($crc -band 1) -eq 1) {
                $crc = ($crc -shr 1) -bxor 0xedb88320
            } else {
                $crc = $crc -shr 1
            }
        }
    }
    return ($crc -bxor 0xffffffff)
}

function Build-VdfEntry([int]$index, [string]$appName, [string]$exePath, [string]$startDir) {
    $crc = Get-Crc32 ($exePath + $appName)
    $appId = [uint32](([int64]$crc -bor 0x80000000L) -band 0xFFFFFFFFL)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # Entry index header
    $bw.Write([byte]0)
    $bw.Write([System.Text.Encoding]::UTF8.GetBytes($index.ToString()))
    $bw.Write([byte]0)

    # Helper functions for VDF fields
    $writeString = {
        param($key, $val)
        $bw.Write([byte]1)
        $bw.Write([System.Text.Encoding]::UTF8.GetBytes($key))
        $bw.Write([byte]0)
        $bw.Write([System.Text.Encoding]::UTF8.GetBytes($val))
        $bw.Write([byte]0)
    }

    $writeInt = {
        param($key, [uint32]$val)
        $bw.Write([byte]2)
        $bw.Write([System.Text.Encoding]::UTF8.GetBytes($key))
        $bw.Write([byte]0)
        $bw.Write([uint32]$val)
    }

    &$writeInt "appid" $appId
    &$writeString "AppName" $appName
    &$writeString "Exe" "`"$exePath`""
    &$writeString "StartDir" "`"$startDir`""
    &$writeString "icon" ""
    &$writeString "ShortcutPath" ""
    &$writeString "LaunchOptions" ""
    &$writeInt "IsHidden" 0
    &$writeInt "AllowDesktopConfig" 1
    &$writeInt "AllowOverlay" 1
    &$writeInt "OpenVR" 0
    &$writeInt "Devkit" 0
    &$writeString "DevkitGameID" ""
    &$writeInt "DevkitOverrideAppID" 0
    &$writeInt "LastPlayTime" 0
    &$writeString "FlatpakAppID" ""

    # Tags sub-section (empty)
    $bw.Write([byte]0)
    $bw.Write([System.Text.Encoding]::UTF8.GetBytes("tags"))
    $bw.Write([byte]0)
    $bw.Write([byte]8)  # close tags sub-section

    # Close this entry
    $bw.Write([byte]8)

    $bytes = $ms.ToArray()
    $bw.Close()
    $ms.Close()
    return $bytes
}

$currDir = Get-Location
$iniPath = Join-Path $currDir "ReFix.ini"

if (-not $GameName) {
    if (Test-Path $iniPath) {
        $line = Get-Content $iniPath | Where-Object { $_ -match "^GameName=" } | Select-Object -First 1
        if ($line) { $GameName = ($line -split "=", 2)[1].Trim() }
    }
}
if (-not $GameName) { $GameName = (Get-Item $currDir).Name }
if (-not $GameName.EndsWith("[ReFix]") -and -not $GameName.EndsWith("[ʀᴇꜰɪx]")) { $displayName = "$GameName [ReFix]" } else { $displayName = $GameName }

if (-not $ExePath) {
    $exeCandidates = Get-ChildItem -Path $currDir -Filter "*.exe" -File -Recurse -ErrorAction SilentlyContinue | 
        Where-Object { $_.Name -notmatch "(?i)(UnityCrash|crashpad|crash_handler|CrashReport|Install|unins|Steamless|generate_interfaces|Configure_LAN_Firewall|Configurar_Firewall|helper|updater)" } |
        Sort-Object Length -Descending
    if ($exeCandidates) { $ExePath = $exeCandidates[0].FullName }
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    Write-Host "[ERROR] Could not find valid game executable." -ForegroundColor Red
    exit 1
}

$startDir = (Split-Path $ExePath -Parent) + "\"

Write-Host "[+] Target Game Name : $displayName" -ForegroundColor Cyan
Write-Host "[+] Executable Path  : $ExePath" -ForegroundColor Cyan
Write-Host "[+] Working Directory: $startDir" -ForegroundColor Cyan

$steamDir = Get-SteamPath

if (-not $steamDir) {
    Write-Host "[ERROR] Could not locate Steam installation." -ForegroundColor Red
    exit 1
}

$userData = Join-Path $steamDir "userdata"

if (-not (Test-Path $userData)) {
    Write-Host "[ERROR] Steam userdata directory not found at $userData" -ForegroundColor Red
    exit 1
}

$userFolders = Get-ChildItem -Path $userData | Where-Object { $_.PSIsContainer -and $_.Name -match "^\d+$" }

foreach ($user in $userFolders) {
    $vdfPath = Join-Path $user.FullName "config\shortcuts.vdf"
    $configDir = Join-Path $user.FullName "config"
    if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }

    if (Test-Path $vdfPath) {
        $raw = [System.IO.File]::ReadAllBytes($vdfPath)
        # Create backup before modifying
        $backupPath = "$vdfPath.refix_backup"
        if (-not (Test-Path $backupPath)) {
            [System.IO.File]::Copy($vdfPath, $backupPath)
            Write-Host "[+] Created backup: shortcuts.vdf.refix_backup" -ForegroundColor DarkGray
        }
    } else {
        # VDF header: \x00 "shortcuts" \x00 followed by footer \x08 \x08
        $raw = [byte[]](0x00, 0x73, 0x68, 0x6f, 0x72, 0x74, 0x63, 0x75, 0x74, 0x73, 0x00, 0x08, 0x08)
    }

    $displayBytes = [System.Text.Encoding]::UTF8.GetBytes($displayName)
    
    # Check if already present
    $alreadyAdded = $false
    for ($i = 0; $i -lt ($raw.Length - $displayBytes.Length); $i++) {
        $match = $true
        for ($j = 0; $j -lt $displayBytes.Length; $j++) {
            if ($raw[$i + $j] -ne $displayBytes[$j]) { $match = $false; break }
        }
        if ($match) { $alreadyAdded = $true; break }
    }

    if (-not $alreadyAdded) {
        # Count existing entries
        $idx = 0
        $appNameHeader = [System.Text.Encoding]::UTF8.GetBytes([char]1 + "AppName" + [char]0)
        for ($i = 0; $i -lt ($raw.Length - $appNameHeader.Length); $i++) {
            $match = $true
            for ($j = 0; $j -lt $appNameHeader.Length; $j++) {
                if ($raw[$i + $j] -ne $appNameHeader[$j]) { $match = $false; break }
            }
            if ($match) { $idx++ }
        }

        # Build new entry (includes its own terminator 0x08)
        $entryBytes = Build-VdfEntry $idx $displayName $ExePath $startDir
        
        # Remove ONLY the 2-byte VDF footer (0x08 0x08) — NOT entry terminators!
        # The VDF structure is: header + entries (each ends with 0x08) + footer (0x08 0x08)
        # We must strip exactly the footer, insert the new entry, then re-add footer.
        $endPos = $raw.Length
        if ($endPos -ge 2 -and $raw[$endPos - 1] -eq 0x08 -and $raw[$endPos - 2] -eq 0x08) {
            $endPos -= 2  # Strip exactly the 2-byte footer
        }
        
        $newRaw = New-Object byte[] ($endPos + $entryBytes.Length + 2)
        [Array]::Copy($raw, 0, $newRaw, 0, $endPos)
        [Array]::Copy($entryBytes, 0, $newRaw, $endPos, $entryBytes.Length)
        # Re-add the 2-byte VDF footer
        $newRaw[$newRaw.Length - 2] = 0x08
        $newRaw[$newRaw.Length - 1] = 0x08

        [System.IO.File]::WriteAllBytes($vdfPath, $newRaw)
        Write-Host "[+] Updated Steam shortcuts for user profile: $($user.Name)" -ForegroundColor Green
    } else {
        Write-Host "[+] Shortcut '$displayName' already exists for profile $($user.Name)" -ForegroundColor Yellow
    }
}
