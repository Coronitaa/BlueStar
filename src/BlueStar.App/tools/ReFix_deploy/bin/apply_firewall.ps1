param(
    [string]$GameExe = "",
    [string]$GameName = "Game",
    [string]$LanPort = "47584",
    [string]$Mode = "goldberg"
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$commands = @()

if ($GameExe -and (Test-Path -LiteralPath $GameExe)) {
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $GameName (TCP In)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $GameName (UDP In)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $GameName (TCP Out)`" >nul 2>&1"
    $commands += "netsh advfirewall firewall delete rule name=`"ReFix - $GameName (UDP Out)`" >nul 2>&1"

    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $GameName (TCP In)`" dir=in action=allow program=`"$GameExe`" protocol=TCP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $GameName (UDP In)`" dir=in action=allow program=`"$GameExe`" protocol=UDP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $GameName (TCP Out)`" dir=out action=allow program=`"$GameExe`" protocol=TCP enable=yes profile=any"
    $commands += "netsh advfirewall firewall add rule name=`"ReFix - $GameName (UDP Out)`" dir=out action=allow program=`"$GameExe`" protocol=UDP enable=yes profile=any"
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
    Write-Host "[INFO] Requesting UAC elevation to configure Windows Firewall rules..." -ForegroundColor Cyan
    $tempScript = [System.IO.Path]::GetTempFileName() + ".bat"
    $batchContent = "@echo off`r`n" + ($commands -join "`r`n") + "`r`nexit`r`n"
    [System.IO.File]::WriteAllText($tempScript, $batchContent)

    try {
        $p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$tempScript`"`"" -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        Write-Host "[OK] Firewall rules applied via UAC." -ForegroundColor Green
    } catch {
        Write-Host "[WARNING] Could not apply Firewall rules: $($_.Exception.Message)" -ForegroundColor Yellow
    } finally {
        if (Test-Path $tempScript) { Remove-Item -Path $tempScript -Force -ErrorAction SilentlyContinue }
    }
}
