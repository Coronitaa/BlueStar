param(
    [string]$GameExe = "",
    [string]$GameName = "Game",
    [string]$LanPort = "47584",
    [string]$Mode = "goldberg"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
if (-not $TargetDir -and $env:BLUESTAR_TARGET_DIR) { $TargetDir = $env:BLUESTAR_TARGET_DIR }
if (-not $BinDir -and $env:BLUESTAR_BIN_DIR) { $BinDir = $env:BLUESTAR_BIN_DIR }
if (-not $ExeDir -and $env:BLUESTAR_EXE_DIR) { $ExeDir = $env:BLUESTAR_EXE_DIR }
if (-not $GameName -and $env:BLUESTAR_GAME_NAME) { $GameName = $env:BLUESTAR_GAME_NAME }
if (-not $UserName -and $env:BLUESTAR_USER_NAME) { $UserName = $env:BLUESTAR_USER_NAME }

if (-not $GameExe -and $env:BLUESTAR_EXE_PATH) { $GameExe = $env:BLUESTAR_EXE_PATH }
if ((-not $GameName -or $GameName -eq "Game") -and $env:BLUESTAR_GAME_NAME) { $GameName = $env:BLUESTAR_GAME_NAME }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$commands = @()
$cleanRuleName = ($GameName -replace '["&|<>^%]', '').Trim()
if (-not $cleanRuleName) { $cleanRuleName = "Game" }

if ($GameExe -and (Test-Path -LiteralPath $GameExe)) {
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $cleanRuleName (TCP In)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $cleanRuleName (UDP In)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $cleanRuleName (TCP Out)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $cleanRuleName (UDP Out)`" >nul 2>&1"

    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $cleanRuleName (TCP In)`" dir=in action=allow program=`"$GameExe`" protocol=TCP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $cleanRuleName (UDP In)`" dir=in action=allow program=`"$GameExe`" protocol=UDP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $cleanRuleName (TCP Out)`" dir=out action=allow program=`"$GameExe`" protocol=TCP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $cleanRuleName (UDP Out)`" dir=out action=allow program=`"$GameExe`" protocol=UDP enable=yes profile=any"
}

if ($Mode -eq "goldberg" -and $LanPort) {
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - Goldberg LAN Discovery (UDP In)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - Goldberg LAN Discovery (UDP Out)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - Goldberg LAN Discovery (UDP In)`" dir=in action=allow protocol=UDP localport=$LanPort enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - Goldberg LAN Discovery (UDP Out)`" dir=out action=allow protocol=UDP remoteport=$LanPort enable=yes profile=any"
}

if ($commands.Count -eq 0) { exit 0 }

if ($isAdmin) {
    foreach ($cmd in $commands) {
        Invoke-Expression "cmd.exe /c `"$cmd`"" | Out-Null
    }
    Write-Host "[OK] Firewall rules configured successfully (Direct Admin)." -ForegroundColor Green
} else {
    Write-Host "[INFO] Attempting to configure Windows Firewall rules without UAC..." -ForegroundColor Cyan
    $tempScript = [System.IO.Path]::GetTempFileName() + ".bat"
    $batchContent = "@echo off`r`n" + ($commands -join "`r`n") + "`r`nexit`r`n"
    [System.IO.File]::WriteAllText($tempScript, $batchContent, [System.Text.Encoding]::UTF8)

    try {
        $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$tempScript`"`"" -WindowStyle Hidden -Wait -PassThru
        Write-Host "[OK] Firewall rules application attempted." -ForegroundColor Green
    } catch {
        Write-Host "[WARNING] Could not apply Firewall rules: $($_.Exception.Message)" -ForegroundColor Yellow
    } finally {
        if (Test-Path -LiteralPath $tempScript) { Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue }
    }
}
