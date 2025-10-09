# Uninstall NpsTray Scheduled Task
# Run this script as Administrator

$ErrorActionPreference = "Stop"

$TaskName = "NpsKiosk_Tray"

Write-Host "Uninstalling NpsTray Scheduled Task..." -ForegroundColor Cyan

$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

if ($existingTask) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "✓ Scheduled Task removed successfully!" -ForegroundColor Green
} else {
    Write-Host "Task '$TaskName' not found. Nothing to uninstall." -ForegroundColor Yellow
}

