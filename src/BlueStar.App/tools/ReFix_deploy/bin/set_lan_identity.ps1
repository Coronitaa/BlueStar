param(
    [Parameter(Mandatory=$true)]
    [string]$ExeDir,
    [Parameter(Mandatory=$true)]
    [string]$SteamId,
    [Parameter(Mandatory=$true)]
    [string]$UserName
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExeDir)) {
    Write-Error "Target executable directory does not exist: $ExeDir"
    exit 1
}

# 1. Update ReFix.ini
$iniPath = Join-Path $ExeDir "ReFix.ini"
if (Test-Path -LiteralPath $iniPath) {
    $lines = Get-Content -LiteralPath $iniPath
    $newLines = @()
    $hasSteamId = $false
    $hasAutoGen = $false
    $hasName = $false

    foreach ($line in $lines) {
        if ($line -match "^\s*SteamId\s*=") {
            $newLines += "SteamId = $SteamId"
            $hasSteamId = $true
        } elseif ($line -match "^\s*AutoGenerateSteamId\s*=") {
            $newLines += "AutoGenerateSteamId = false"
            $hasAutoGen = $true
        } elseif ($line -match "^\s*Name\s*=") {
            $newLines += "Name = $UserName"
            $hasName = $true
        } else {
            $newLines += $line
        }
    }

    if (-not $hasSteamId) { $newLines += "SteamId = $SteamId" }
    if (-not $hasAutoGen) { $newLines += "AutoGenerateSteamId = false" }
    if (-not $hasName)    { $newLines += "Name = $UserName" }

    Set-Content -LiteralPath $iniPath -Value $newLines -Encoding UTF8
    Write-Host "[OK] Updated ReFix.ini with SteamId=$SteamId, Name=$UserName" -ForegroundColor Green
}

# 2. Update steam_settings
$settingsDir = Join-Path $ExeDir "steam_settings"
if (-not (Test-Path -LiteralPath $settingsDir)) {
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
}

Set-Content -LiteralPath (Join-Path $settingsDir "force_steamid.txt") -Value $SteamId -Encoding ASCII
Set-Content -LiteralPath (Join-Path $settingsDir "user_steam_id.txt") -Value $SteamId -Encoding ASCII
Set-Content -LiteralPath (Join-Path $settingsDir "force_account_name.txt") -Value $UserName -Encoding UTF8

$configsUserPath = Join-Path $settingsDir "configs.user.ini"
if (Test-Path -LiteralPath $configsUserPath) {
    $cuLines = Get-Content -LiteralPath $configsUserPath
    $newCuLines = @()
    foreach ($cl in $cuLines) {
        if ($cl -match "^\s*account_name\s*=") {
            $newCuLines += "account_name=$UserName"
        } elseif ($cl -match "^\s*account_steamid\s*=") {
            $newCuLines += "account_steamid=$SteamId"
        } else {
            $newCuLines += $cl
        }
    }
    Set-Content -LiteralPath $configsUserPath -Value $newCuLines -Encoding UTF8
}
Write-Host "[OK] Updated steam_settings with force_steamid.txt, user_steam_id.txt, force_account_name.txt and configs.user.ini" -ForegroundColor Green

exit 0
