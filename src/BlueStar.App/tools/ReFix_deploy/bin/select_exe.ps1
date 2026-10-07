param(
    [string]$InitialDir = ""
)
if (-not $InitialDir -and $env:REFIX_EXE_DIR) {
    $InitialDir = $env:REFIX_EXE_DIR
}
if (-not $InitialDir -and $env:REFIX_TARGET_DIR) {
    $InitialDir = $env:REFIX_TARGET_DIR
}
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = "Select Game Executable"
$dialog.Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*"
if ($InitialDir -and (Test-Path -LiteralPath $InitialDir)) {
    $dialog.InitialDirectory = $InitialDir
}
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
    Write-Output $dialog.FileName
}
