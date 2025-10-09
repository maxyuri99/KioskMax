# Build and publish all NPS Kiosk components
# Generates self-contained executables ready for deployment

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build/publish"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  NPS Kiosk Build and Publish Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$projects = @(
    @{Name="NpsDisplay"; Path="src/NpsDisplay/NpsDisplay.csproj"},
    @{Name="NpsTray"; Path="src/NpsTray/NpsTray.csproj"},
    @{Name="NpsWatchdog"; Path="src/NpsWatchdog/NpsWatchdog.csproj"},
    @{Name="WindowRouter"; Path="src/WindowRouter/WindowRouter.csproj"}
)

foreach ($project in $projects) {
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Yellow
    
    $outputPath = "$OutputDir/$($project.Name)"
    
    dotnet publish $project.Path `
        -c $Configuration `
        -r $Runtime `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -o $outputPath
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✓ $($project.Name) published successfully" -ForegroundColor Green
    } else {
        Write-Host "✗ Failed to publish $($project.Name)" -ForegroundColor Red
        exit 1
    }
    
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Published files are in: $OutputDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Copy the published folders to target machine" -ForegroundColor White
Write-Host "2. Ensure WebView2 Runtime is installed" -ForegroundColor White
Write-Host "3. Run install-scheduled-task.ps1 (for NpsTray)" -ForegroundColor White
Write-Host "4. (Optional) Run install-watchdog-service.ps1" -ForegroundColor White
Write-Host ""

