# Uninstall NpsWatchdog Windows Service
# Run this script as Administrator

$ErrorActionPreference = "Stop"

$ServiceName = "NpsWatchdog"

Write-Host "Uninstalling NpsWatchdog Windows Service..." -ForegroundColor Cyan

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    
    Write-Host "Removing service..." -ForegroundColor Yellow
    sc.exe delete $ServiceName
    
    Write-Host "✓ Service removed successfully!" -ForegroundColor Green
} else {
    Write-Host "Service '$ServiceName' not found. Nothing to uninstall." -ForegroundColor Yellow
}

