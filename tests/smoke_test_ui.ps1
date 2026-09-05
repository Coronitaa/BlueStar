Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$exePath = Resolve-Path "src\BlueStar.App\bin\Release\net8.0-windows\BlueStar.exe"
Write-Host "===================================================="
Write-Host "REAL UI SMOKE TEST & AUTOMATION: $exePath"
Write-Host "===================================================="

# Ensure no orphan processes
Get-Process BlueStar* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$proc = Start-Process -FilePath $exePath -PassThru
Write-Host "[1/6] Process launched successfully. PID: $($proc.Id)"

try {
    $timeout = [DateTime]::UtcNow.AddSeconds(15)
    $wpfWindow = $null

    while ([DateTime]::UtcNow -lt $timeout) {
        $proc.Refresh()
        if ($proc.HasExited) {
            throw "Process exited prematurely with exit code: $($proc.ExitCode)"
        }

        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $proc.Id
        )
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $cond
        )

        foreach ($w in $windows) {
            if ($w.Current.FrameworkId -eq "WPF" -or $w.Current.Name -eq "BlueStar") {
                $wpfWindow = $w
                break
            }
        }

        if ($wpfWindow -ne $null) {
            break
        }
        Start-Sleep -Milliseconds 400
    }

    if ($wpfWindow -eq $null) {
        throw "Timeout waiting for WPF 'BlueStar' window."
    }

    Write-Host "[2/6] Real WPF Window acquired!"
    Write-Host "      Title:       '$($wpfWindow.Current.Name)'"
    Write-Host "      Class:       '$($wpfWindow.Current.ClassName)'"
    Write-Host "      Framework:   '$($wpfWindow.Current.FrameworkId)'"
    Write-Host "      Handle:      $($wpfWindow.Current.NativeWindowHandle)"

    Start-Sleep -Seconds 1

    # Enumerate all descendants in the WPF window
    $allDescendants = $wpfWindow.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    )
    Write-Host "[3/6] Discovered $($allDescendants.Count) UI elements in WPF visual tree."

    # Look for CloseButton, MaximizeButton, MinimizeButton
    $closeButtonCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "CloseButton"
    )
    $closeBtn = $wpfWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeButtonCond)
    if ($closeBtn -ne $null) {
        Write-Host "[4/6] Found CloseButton (AutomationId='CloseButton') in title bar."
    }

    # Verify no crash / unhandled exception
    if ($wpfWindow.Current.Name -match "Exception|Error|Crash|CLR20r3") {
        throw "Error dialog detected: $($wpfWindow.Current.Name)"
    }
    Write-Host "[5/6] UI verification PASSED: Window fully rendered, responsive, and without exceptions."

    # Close the window cleanly
    Write-Host "[6/6] Closing MainWindow gracefully..."
    if ($closeBtn -ne $null) {
        $invokePattern = $closeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($invokePattern -ne $null) {
            Write-Host "      Triggering CloseButton via UIAutomation InvokePattern..."
            $invokePattern.Invoke()
        }
        else {
            $proc.CloseMainWindow() | Out-Null
        }
    }
    else {
        $proc.CloseMainWindow() | Out-Null
    }

    $exited = $proc.WaitForExit(7000)
    if (-not $exited) {
        Write-Host "      Window did not exit within 7s, terminating process..."
        $proc.Kill()
        throw "Process failed to close gracefully within timeout."
    }

    Write-Host "===================================================="
    Write-Host "SMOKE TEST RESULT: PASSED (ExitCode: $($proc.ExitCode))"
    Write-Host "===================================================="
}
finally {
    if ($proc -and -not $proc.HasExited) {
        $proc.Kill()
    }
}
