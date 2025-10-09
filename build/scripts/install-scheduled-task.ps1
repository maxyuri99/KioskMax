# Install NpsTray as a Scheduled Task to auto-start on user logon
# Run this script as Administrator

param(
    [string]$InstallPath = "C:\Program Files\UpMais\NpsKiosk\Tray\NpsTray.exe"
)

$ErrorActionPreference = "Stop"

Write-Host "Installing NpsTray Scheduled Task..." -ForegroundColor Cyan

# Check if executable exists
if (-not (Test-Path $InstallPath)) {
    Write-Host "ERROR: NpsTray.exe not found at: $InstallPath" -ForegroundColor Red
    Write-Host "Please specify the correct path using -InstallPath parameter" -ForegroundColor Yellow
    exit 1
}

# Task name
$TaskName = "NpsKiosk_Tray"

# Delete existing task if it exists
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Write-Host "Removing existing task..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Create action
$Action = New-ScheduledTaskAction -Execute $InstallPath

# Create trigger (at logon of any user)
$Trigger = New-ScheduledTaskTrigger -AtLogOn

# Create principal (run with highest privileges)
$Principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

# Create settings
$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)

# Register the task
Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $Action `
    -Trigger $Trigger `
    -Principal $Principal `
    -Settings $Settings `
    -Description "NPS Kiosk System Tray Controller - Auto-starts NpsDisplay on user logon" `
    -Force

Write-Host "✓ Scheduled Task installed successfully!" -ForegroundColor Green
Write-Host "Task Name: $TaskName" -ForegroundColor Cyan
Write-Host "Executable: $InstallPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "The task will run automatically on next user logon." -ForegroundColor Green
Write-Host "To start it now, run: Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Yellow

