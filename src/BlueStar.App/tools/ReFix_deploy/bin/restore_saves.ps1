# Uninstall helper: gives a game its own PC's saves back.
#
# With ReFix LAN profiles the game's known save folder (Unity LocalLow\<Company>\<Product>, Unreal
# %LOCALAPPDATA%\<Project>\Saved\SaveGames) holds the saves of whichever profile played last; the others
# are kept in %APPDATA%\ReFix. Without ReFix the game only sees that folder, so before uninstalling it
# gets the saves it had before profiles: those of the profile created with "use the saves already on this
# PC", or %APPDATA%\ReFix\Saves\_pc. The saves that were in place go to their profile. Nothing is
# deleted, and the profiles stay (every ReFix game of this PC shares them).
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File restore_saves.ps1 -GameDir "<game folder>"

param([Parameter(Mandatory = $true)][string]$GameDir)

$ErrorActionPreference = "Stop"
# %APPDATA%\ReFix since 2.1; %APPDATA%\ReFix LAN when the game never started with 2.1 (not migrated yet).
$root = Join-Path $env:APPDATA "ReFix"
if (-not (Test-Path -LiteralPath (Join-Path $root "saves.ini")) -and (Test-Path -LiteralPath (Join-Path $env:APPDATA "ReFix LAN\saves.ini"))) {
    $root = Join-Path $env:APPDATA "ReFix LAN"
}
$ownersIni = Join-Path $root "saves.ini"
if (-not (Test-Path -LiteralPath $ownersIni)) { exit 0 }   # profiles never swapped saves on this PC

function Sanitize([string]$s) {
    $chars = $s.ToCharArray() | ForEach-Object { if ('<>:"/\|?*'.Contains([string]$_) -or [int]$_ -lt 32) { '_' } else { $_ } }
    return -join $chars
}

# Same detection as save_swap.cpp, from the game folder instead of the running exe.
$locations = @()
$localLow = Join-Path $env:USERPROFILE "AppData\LocalLow"
Get-ChildItem -LiteralPath $GameDir -Recurse -Depth 3 -Filter "app.info" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -like "*_Data" } | ForEach-Object {
        $lines = [System.IO.File]::ReadAllLines($_.FullName, [System.Text.Encoding]::UTF8)
        if ($lines.Count -ge 2 -and $lines[0].Trim() -and $lines[1].Trim()) {
            $company = $lines[0].Trim(); $product = $lines[1].Trim()
            $locations += [pscustomobject]@{
                Label = "Unity_$(Sanitize $company)_$(Sanitize $product)"
                Live  = Join-Path $localLow (Join-Path $company $product)
                Keep  = @("Player.log", "Player-prev.log", "output_log.txt", "Unity")
            }
        }
    }
Get-ChildItem -LiteralPath $GameDir -Recurse -Depth 4 -Directory -Filter "Win64" -ErrorAction SilentlyContinue |
    Where-Object { $_.Parent.Name -eq "Binaries" } | ForEach-Object {
        $project = $_.Parent.Parent
        if ($project.Name -ne "Engine" -and ((Test-Path (Join-Path $project.Parent.FullName "Engine")) -or (Test-Path (Join-Path $project.FullName "Content")))) {
            $locations += [pscustomobject]@{
                Label = "UE_$(Sanitize $project.Name)"
                Live  = Join-Path $env:LOCALAPPDATA (Join-Path $project.Name "Saved\SaveGames")
                Keep  = @()
            }
        }
    }
if ($locations.Count -eq 0) { exit 0 }

# saves.ini is UTF-16 (written with a BOM by the overlay); keep its encoding when writing back.
$bytes = [System.IO.File]::ReadAllBytes($ownersIni)
$unicode = $bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE
$encoding = if ($unicode) { [System.Text.Encoding]::Unicode } else { [System.Text.Encoding]::Default }
$iniLines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines($ownersIni, $encoding))
function OwnerOf([string]$label) {
    foreach ($l in $iniLines) { if ($l -match "^\s*$([regex]::Escape($label))\s*=\s*(.+?)\s*$") { return $Matches[1] } }
    return ""
}
function SetOwner([string]$label, [string]$owner) {
    for ($i = 0; $i -lt $iniLines.Count; $i++) {
        if ($iniLines[$i] -match "^\s*$([regex]::Escape($label))\s*=") { $iniLines[$i] = "$label=$owner"; return }
    }
}

# The profile that kept this PC's saves (NativeSaves=1), else the pre-profile stash.
$native = "_pc"
Get-ChildItem -LiteralPath (Join-Path $root "Profiles") -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $ini = Join-Path $_.FullName "profile.ini"
    if ((Test-Path -LiteralPath $ini) -and ((Get-Content -LiteralPath $ini) -match "^\s*NativeSaves\s*=\s*1")) { $native = $_.Name }
}
function StashOf([string]$owner, [string]$label) {
    if ($owner -eq "_pc") { return Join-Path $root (Join-Path "Saves\_pc" $label) }
    return Join-Path $root (Join-Path "Profiles\$owner\Saves" $label)
}

$changed = $false
foreach ($loc in $locations) {
    $owner = OwnerOf $loc.Label
    if (-not $owner -or $owner -eq $native) { continue }
    $out = StashOf $owner $loc.Label
    $in = StashOf $native $loc.Label
    $live = @(Get-ChildItem -LiteralPath $loc.Live -Force -ErrorAction SilentlyContinue | Where-Object { $loc.Keep -notcontains $_.Name })
    $clash = @($live | Where-Object { Test-Path -LiteralPath (Join-Path $out $_.Name) })
    if ($clash.Count -gt 0) {
        Write-Host "  [WARNING] $($loc.Label): $out already has files with the same names; saves left as they are" -ForegroundColor Yellow
        continue
    }
    try {
        New-Item -ItemType Directory -Path $out -Force | Out-Null
        $live | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination $out }
        New-Item -ItemType Directory -Path $loc.Live -Force | Out-Null
        Get-ChildItem -LiteralPath $in -Force -ErrorAction SilentlyContinue | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination $loc.Live }
    } catch {
        Write-Host "  [WARNING] $($loc.Label): could not move the saves (is the game still running?): $_" -ForegroundColor Yellow
        continue
    }
    SetOwner $loc.Label $native
    $changed = $true
    Write-Host "  [OK] $($loc.Label): this PC's saves are back in $($loc.Live); profile $owner keeps its own in $out" -ForegroundColor Green
}
if ($changed) { [System.IO.File]::WriteAllLines($ownersIni, $iniLines, $encoding) }
exit 0
