param(
    [Parameter(Mandatory=$false)]
    [string]$TargetDir = ""
)

if (-not $TargetDir -and $env:REFIX_TARGET_DIR) {
    $TargetDir = $env:REFIX_TARGET_DIR
}

if (-not $TargetDir) {
    Write-Output "ERROR=Target directory parameter is missing"
    exit 1
}

$target = $TargetDir.Trim().Trim('"').TrimEnd('\').TrimEnd('/')
if (-not (Test-Path -LiteralPath $target)) {
    Write-Output "ERROR=Directory not found: $target"
    exit 1
}

$ignorePatterns = @(
    "*crashpad*", "*UnityCrashHandler*", "*unins*", "*setup*", "*updater*",
    "*vcredist*", "*dxsetup*", "*dotnet*", "*prereq*", "*F10.exe*",
    "*.unpacked.exe*", "*.steamstub.exe*", "*.original.exe*", "*redist*",
    "*EasyAntiCheat*", "*eac_server*", "*BEService*", "*BattlEye*", "*cefprocess*",
    "*CefSharp*", "*QtWebEngineProcess*", "*createdump*",
    "*EOSBootstrapper*", "*gamelaunchhelper*",
    # ReFix's own helpers (Steam host, Fusion Shared server, mods tool) are never the game
    "*refix_steamhost*", "*ReFixFusionShared*", "*refixmod*"
)

$deprioritizePatterns = @(
    "*dedicated*", "*nullrenderer*", "*server*", "*cmd.exe*", "*cli.exe*",
    "*benchmark*", "*test*.exe*", "*tool*.exe*", "*editor*", "*config*.exe*",
    "*helper*.exe*", "*report*.exe*",
    "*_eac.exe*", "*_eac_*.exe*", "*eac_launcher*.exe*", "*start_protected_game*.exe*",
    "*eosbootstrapper*", "*gamelaunchhelper*"
)

# 1. Detect Engine Type
$engineType = "Native"

# Unity detection
$unityData = Get-ChildItem -LiteralPath $target -Directory -Filter "*_Data" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$unityDll = Get-ChildItem -LiteralPath $target -File -Filter "UnityPlayer.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($unityData -or $unityDll) {
    $engineType = "Unity"
}

# Unreal detection
if (Test-Path -LiteralPath (Join-Path $target "Engine")) {
    $engineType = "Unreal"
}
$unrealShipping = Get-ChildItem -LiteralPath $target -File -Filter "*-Win64-Shipping.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($unrealShipping) {
    $engineType = "Unreal"
}

# Godot detection
$pck = Get-ChildItem -LiteralPath $target -Filter "*.pck" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
$godotDll = Get-ChildItem -LiteralPath $target -Filter "libgodot*.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pck -or $godotDll) {
    $engineType = "Godot"
}

# Source Engine detection (Source 1 / Source 2)
$sourceEngine = (Test-Path -LiteralPath (Join-Path $target "bin\engine.dll")) -or `
                (Test-Path -LiteralPath (Join-Path $target "bin\client.dll")) -or `
                (Test-Path -LiteralPath (Join-Path $target "bin\tier0.dll")) -or `
                (Get-ChildItem -LiteralPath $target -Filter "gameinfo.txt" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or `
                (Get-ChildItem -LiteralPath $target -Filter "gameinfo.gi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($sourceEngine) {
    $engineType = "Source"
}

# Mono / .NET detection (e.g. Barotrauma, Terraria, Stardew Valley)
if ($engineType -eq "Native") {
    $monoOrDotNet = (Get-ChildItem -LiteralPath $target -Filter "MonoPosixHelper.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or `
                    (Get-ChildItem -LiteralPath $target -Filter "mono-2.0*.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or `
                    (Get-ChildItem -LiteralPath $target -Filter "hostfxr.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or `
                    (Get-ChildItem -LiteralPath $target -Filter "coreclr.dll" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) -or `
                    (Get-ChildItem -LiteralPath $target -Filter "*.runtimeconfig.json" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($monoOrDotNet) {
        $engineType = "Mono"
    }
}

# 2. Find all steam_api64.dll or steam_api.dll locations
$steamDlls = Get-ChildItem -LiteralPath $target -Filter "steam_api*.dll" -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "*valve*" -and $_.Name -notlike "*goldberg*" -and $_.Name -notlike "*_o.dll*" }

$allExes = Get-ChildItem -LiteralPath $target -Filter "*.exe" -Recurse -File -ErrorAction SilentlyContinue
$validExes = @()

foreach ($e in $allExes) {
    $isIgnored = $false
    foreach ($pat in $ignorePatterns) {
        if ($e.Name -like $pat) { $isIgnored = $true; break }
    }
    if (-not $isIgnored) {
        $validExes += $e
    }
}

# Score executables to find the primary game executable:
# Higher score = higher priority
$scoredExes = @()
$targetFolderItem = Get-Item -LiteralPath $target
$targetFolderName = $targetFolderItem.Name.ToLower().Replace(" ", "").Replace("'", "").Replace("-", "").Replace("_", "")

foreach ($exe in $validExes) {
    $score = 100
    $exeNameLower = $exe.BaseName.ToLower().Replace(" ", "").Replace("'", "").Replace("-", "").Replace("_", "")
    $exeDirLower = $exe.DirectoryName.ToLower()

    # Penalize server / dedicated / nullrenderer / tool exes
    foreach ($dpat in $deprioritizePatterns) {
        if ($exe.Name -like $dpat) {
            $score -= 200
            break
        }
    }

    # Heavily penalize anti-cheat wrappers / protected game starters if a real game binary exists
    if ($exe.Name -match "(?i)(_eac\.exe|eac_launcher\.exe|start_protected_game\.exe)") {
        $score -= 300
    }

    # Bonus if executable name closely matches the target folder name
    if ($exeNameLower -like "*$targetFolderName*" -or $targetFolderName -like "*$exeNameLower*") {
        $score += 80
    }

    # Bonus for common primary client naming patterns
    if ($exe.Name -match "(?i)(client|game|shipping|win64_shipping|steam|together)") {
        $score += 40
    }

    # Bonus for 64-bit binaries
    if ($exe.Name -match "(?i)(x64|64|win64)") {
        $score += 30
    }

    # Bonus for being in standard binary directories (bin64, bin, Binaries\Win64)
    if ($exeDirLower -match "(bin64|binaries\\win64)") {
        $score += 50
    } elseif ($exeDirLower -match "\\bin$") {
        $score += 30
    } elseif ($exe.DirectoryName -eq $target) {
        $score += 25
    }

    # Bonus if steam_api64.dll or steam_api.dll is in the same directory
    if ((Test-Path -LiteralPath (Join-Path $exe.DirectoryName "steam_api64.dll")) -or (Test-Path -LiteralPath (Join-Path $exe.DirectoryName "steam_api.dll"))) {
        $score += 60
    }

    $scoredExes += [PSCustomObject]@{
        Exe = $exe
        Score = $score
    }
}

$sortedExes = @($scoredExes | Sort-Object -Property Score -Descending)

$chosenExe = if ($sortedExes.Count -gt 0) { $sortedExes[0].Exe } else { $null }

# Determine EXE_DIR and GAME_NAME
$exeDir = $target
$gameExePath = ""
$gameName = (Get-Item -LiteralPath $target).Name

if ($chosenExe) {
    $gameExePath = $chosenExe.FullName
    $exeDir = $chosenExe.DirectoryName
    
    $cleanName = $chosenExe.BaseName
    if ($cleanName -like "*-Win64-Shipping") {
        $cleanName = $cleanName.Replace("-Win64-Shipping", "")
    }
    if ($cleanName -notmatch "(?i)^(game|launch|main|start|dontstarve_dedicated_server_nullrenderer)$") {
        $gameName = $cleanName
    }
}

if ($gameName -like "*_Windows") {
    $gameName = $gameName.Substring(0, $gameName.Length - 8)
} elseif ($gameName -like "*_Win64") {
    $gameName = $gameName.Substring(0, $gameName.Length - 6)
}

# 3. Detect Original AppID across all potential files
$detectedAppId = ""

# A. Scan for steam_appid.txt
$appIdFiles = Get-ChildItem -LiteralPath $target -Filter "steam_appid.txt" -Recurse -File -ErrorAction SilentlyContinue
foreach ($f in $appIdFiles) {
    $content = (Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue)
    if ($content -match "([0-9]{3,9})") {
        $found = $Matches[1].Trim()
        if ($found -ne "480" -or -not $detectedAppId) {
            $detectedAppId = $found
            if ($found -ne "480") { break }
        }
    }
}

# B. Scan for ReFix.ini RealAppId
if (-not $detectedAppId -or $detectedAppId -eq "480") {
    $reFixInis = Get-ChildItem -LiteralPath $target -Filter "ReFix.ini" -Recurse -File -ErrorAction SilentlyContinue
    foreach ($ini in $reFixInis) {
        $lines = Get-Content -LiteralPath $ini.FullName -ErrorAction SilentlyContinue
        foreach ($line in $lines) {
            if ($line -match "^\s*RealAppId\s*=\s*([0-9]+)") {
                $found = $Matches[1].Trim()
                if ($found -and $found -ne "0" -and $found -ne "480") {
                    $detectedAppId = $found
                    break
                }
            }
        }
        if ($detectedAppId -and $detectedAppId -ne "480") { break }
    }
}

# C. Scan for steam_settings\configs.app.ini
if (-not $detectedAppId -or $detectedAppId -eq "480") {
    $appInis = Get-ChildItem -LiteralPath $target -Filter "configs.app.ini" -Recurse -File -ErrorAction SilentlyContinue
    foreach ($ini in $appInis) {
        $lines = Get-Content -LiteralPath $ini.FullName -ErrorAction SilentlyContinue
        foreach ($line in $lines) {
            if ($line -match "^\s*appid\s*=\s*([0-9]+)") {
                $found = $Matches[1].Trim()
                if ($found -and $found -ne "0" -and $found -ne "480") {
                    $detectedAppId = $found
                    break
                }
            }
        }
        if ($detectedAppId -and $detectedAppId -ne "480") { break }
    }
}

# 4. Check for presence of Steam and EOS
$hasSteam = $false
if ($steamDlls.Count -gt 0) { $hasSteam = $true }

$hasEos = $false
$eosFiles = Get-ChildItem -LiteralPath $target -Filter "*EOSSDK*" -Recurse -File -ErrorAction SilentlyContinue
if ($eosFiles.Count -gt 0) { $hasEos = $true }
$redpointFolders = Get-ChildItem -LiteralPath $target -Filter "*Redpoint*" -Recurse -Directory -ErrorAction SilentlyContinue
if ($redpointFolders.Count -gt 0) { $hasEos = $true }

# 4b. Check for presence of EasyAntiCheat (EAC)
$hasEac = $false
$eacDirs = Get-ChildItem -LiteralPath $target -Filter "*EasyAntiCheat*" -Recurse -Directory -ErrorAction SilentlyContinue
$eacFiles = Get-ChildItem -LiteralPath $target -Filter "*EasyAntiCheat*" -Recurse -File -ErrorAction SilentlyContinue
$eacSetup = Get-ChildItem -LiteralPath $target -Filter "*start_protected_game*" -Recurse -File -ErrorAction SilentlyContinue
if (($eacDirs.Count -gt 0) -or ($eacFiles.Count -gt 0) -or ($eacSetup.Count -gt 0)) {
    $hasEac = $true
}

# 4c. Detect Store Platform (steam | epic | msstore) and GDK runtime
$storeType = "steam"
$hasGdk = $false

$msConfig = Get-ChildItem -LiteralPath $target -Filter "MicrosoftGame.Config" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
$appxManifest = Get-ChildItem -LiteralPath $target -Filter "appxmanifest.xml" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
$gdkHelper = Get-ChildItem -LiteralPath $target -Filter "gamelaunchhelper.exe" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
$gdkDlls = Get-ChildItem -LiteralPath $target -Filter "*XGamingRuntime*" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
$xboxDlls = Get-ChildItem -LiteralPath $target -Filter "*Microsoft*Xbox*Services*" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gdkDlls -or $xboxDlls -or $msConfig -or $appxManifest -or $gdkHelper) {
    $hasGdk = $true
}

$egStoreDir = Get-ChildItem -LiteralPath $target -Filter ".egstore" -Recurse -Directory -Force -ErrorAction SilentlyContinue | Select-Object -First 1
$eosBootstrap = Get-ChildItem -LiteralPath $target -Filter "EOSBootstrapper.*" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1

if ($msConfig -or $appxManifest -or $gdkHelper) {
    $storeType = "msstore"
    if ($msConfig) {
        try {
            [xml]$mgXml = Get-Content -LiteralPath $msConfig.FullName -Raw -ErrorAction Stop
            $dispName = $mgXml.Game.ShellVisuals.DefaultDisplayName
            if ($dispName -and $dispName -notmatch '^ms-resource:') {
                $gameName = $dispName.Trim()
            }
            if (-not $chosenExe) {
                $mgExeName = $mgXml.Game.ExecutableList.Executable | Select-Object -First 1 -ExpandProperty Name
                if ($mgExeName) {
                    $cand = Join-Path $msConfig.DirectoryName $mgExeName
                    if (Test-Path -LiteralPath $cand) {
                        $chosenExe = Get-Item -LiteralPath $cand -ErrorAction SilentlyContinue
                        $gameExePath = $cand
                        $exeDir = $msConfig.DirectoryName
                    }
                }
            }
        } catch {}
    }
    if ($appxManifest) {
        try {
            [xml]$axXml = Get-Content -LiteralPath $appxManifest.FullName -Raw -ErrorAction Stop
            if (-not $gameName -or $gameName -eq "Content") {
                $axDisp = $axXml.Package.Properties.DisplayName
                if ($axDisp -and $axDisp -notmatch '^ms-resource:') {
                    $gameName = $axDisp.Trim()
                }
            }
        } catch {}
    }
} elseif ($egStoreDir -or $eosBootstrap) {
    $storeType = "epic"
    $localItems = @()
    if ($egStoreDir) {
        $localItems = @(Get-ChildItem -LiteralPath $egStoreDir.FullName -Filter "*.item" -File -Force -ErrorAction SilentlyContinue)
    }
    $launcherItems = @()
    $epicLauncherManifests = "C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests"
    if (Test-Path -LiteralPath $epicLauncherManifests) {
        $launcherItems = @(Get-ChildItem -LiteralPath $epicLauncherManifests -Filter "*.item" -File -ErrorAction SilentlyContinue)
    }
    foreach ($it in ($localItems + $launcherItems)) {
        try {
            $json = Get-Content -LiteralPath $it.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            $isLocalItem = $egStoreDir -and ($it.DirectoryName -eq $egStoreDir.FullName)
            $matchesLoc = $json.InstallLocation -and ($json.InstallLocation.TrimEnd('\','/') -ieq $target)
            if ($isLocalItem -or $matchesLoc) {
                if ($json.DisplayName) { $gameName = $json.DisplayName.Trim() }
                if (-not $chosenExe -and $json.LaunchExecutable) {
                    $cand = Join-Path $target $json.LaunchExecutable
                    if (Test-Path -LiteralPath $cand) {
                        $chosenExe = Get-Item -LiteralPath $cand -ErrorAction SilentlyContinue
                        $gameExePath = $cand
                        $exeDir = Split-Path -Parent $cand
                    }
                }
                break
            }
        } catch {}
    }
}

# 5. Architecture detection (x86 vs x64)
# First inspect chosen executable PE header directly (0x014c = x86, 0x8664 = x64)
$isX86 = $false
$peDetected = $false
if ($chosenExe -and (Test-Path -LiteralPath $chosenExe.FullName)) {
    try {
        $peBytes = [System.IO.File]::ReadAllBytes($chosenExe.FullName)
        if ($peBytes.Length -ge 0x40) {
            $peOffset = [System.BitConverter]::ToInt32($peBytes, 0x3C)
            if ($peOffset + 6 -le $peBytes.Length) {
                $mach = [System.BitConverter]::ToUInt16($peBytes, $peOffset + 4)
                if ($mach -eq 0x014c) {
                    $isX86 = $true
                    $peDetected = $true
                } elseif ($mach -eq 0x8664) {
                    $isX86 = $false
                    $peDetected = $true
                }
            }
        }
    } catch {}
}

if (-not $peDetected) {
    $steamApi32s = Get-ChildItem -LiteralPath $target -Filter "steam_api.dll" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "steam_api.dll" }
    $steamApi64s = Get-ChildItem -LiteralPath $target -Filter "steam_api64.dll" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike "*valve*" -and $_.Name -notlike "*_o.dll*" }
    $pluginsX86Dirs = Get-ChildItem -LiteralPath $target -Directory -Filter "x86" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Parent.Name -eq "Plugins" }
    $pluginsX64Dirs = Get-ChildItem -LiteralPath $target -Directory -Filter "x86_64" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Parent.Name -eq "Plugins" }

    if ($steamApi32s.Count -gt 0 -and $steamApi64s.Count -eq 0) { $isX86 = $true }
    if ($pluginsX86Dirs.Count -gt 0 -and $pluginsX64Dirs.Count -eq 0) { $isX86 = $true }
    if ($appxManifest) {
        try {
            [xml]$axArch = Get-Content -LiteralPath $appxManifest.FullName -Raw -ErrorAction Stop
            $procArch = $axArch.Package.Identity.ProcessorArchitecture
            if ($procArch -eq "x86") { $isX86 = $true }
            elseif ($procArch -eq "x64") { $isX86 = $false }
        } catch {}
    }
}

# Build candidate exe list for interactive picker (up to top 5)
$candidateList = @()
foreach ($item in ($sortedExes | Select-Object -First 5)) {
    $candidateList += $item.Exe.FullName
}
$candidateListStr = $candidateList -join "|"

# Output key=value pairs for easy parsing in CMD
Write-Output "ENGINE_TYPE=$engineType"
Write-Output "EXE_DIR=$exeDir"
Write-Output "GAME_EXE_PATH=$gameExePath"
Write-Output "GAME_NAME=$gameName"
Write-Output "DETECTED_APPID=$detectedAppId"
Write-Output "HAS_STEAM=$hasSteam"
Write-Output "HAS_EOS=$hasEos"
Write-Output "HAS_EAC=$hasEac"
Write-Output "HAS_GDK=$hasGdk"
Write-Output "STORE_TYPE=$storeType"
Write-Output "IS_X86=$isX86"
Write-Output "CANDIDATE_EXES=$candidateListStr"
