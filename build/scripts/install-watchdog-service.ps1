# Install NpsWatchdog as a Windows Service
# Run this script as Administrator

param(
    [string]$InstallPath = "C:\Program Files\UpMais\NpsKiosk\Watchdog\NpsWatchdog.exe"
)

$ErrorActionPreference = "Stop"

Write-Host "Installing NpsWatchdog Windows Service..." -ForegroundColor Cyan

# Check if executable exists
if (-not (Test-Path $InstallPath)) {
    Write-Host "ERROR: NpsWatchdog.exe not found at: $InstallPath" -ForegroundColor Red
    Write-Host "Please specify the correct path using -InstallPath parameter" -ForegroundColor Yellow
    exit 1
}

$ServiceName = "NpsWatchdog"
$DisplayName = "NPS Kiosk Watchdog"
$Description = "Monitors and automatically restarts the NPS Display application if it crashes or becomes unresponsive"

# Check if service already exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
    
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

# Create the service
Write-Host "Creating service..." -ForegroundColor Cyan

sc.exe create $ServiceName `
    binPath= $InstallPath `
    start= delayed-auto `
    DisplayName= $DisplayName `
    | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create service (Exit code: $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

# Set description
sc.exe description $ServiceName $Description | Out-Null

# Configure recovery options (restart on failure)
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "✓ Service installed successfully!" -ForegroundColor Green
Write-Host "Service Name: $ServiceName" -ForegroundColor Cyan
Write-Host "Display Name: $DisplayName" -ForegroundColor Cyan
Write-Host "Executable: $InstallPath" -ForegroundColor Cyan
Write-Host "Start Type: Automatic (Delayed)" -ForegroundColor Cyan
Write-Host ""
Write-Host "To start the service now, run:" -ForegroundColor Yellow
Write-Host "  Start-Service -Name $ServiceName" -ForegroundColor White
Write-Host ""
Write-Host "To check service status, run:" -ForegroundColor Yellow
Write-Host "  Get-Service -Name $ServiceName" -ForegroundColor White

