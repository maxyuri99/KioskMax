param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build/publish",
    [string]$Version = "2.0.0",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  NPS Kiosk Portable Bundle Publisher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

$bundleDir = Join-Path $OutputDir "NpsKiosk-Portable"
if (Test-Path $bundleDir) { Remove-Item $bundleDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

$projects = @(
    @{Name="KioskBrowser"; Path="src/KioskBrowser/KioskBrowser.csproj"; Subdir="Display"},
    @{Name="KioskController"; Path="src/KioskController/KioskController.csproj"; Subdir="Tray"},
    @{Name="NpsWatchdog"; Path="src/NpsWatchdog/NpsWatchdog.csproj"; Subdir="Watchdog"},
    @{Name="WindowRouter"; Path="src/WindowRouter/WindowRouter.csproj"; Subdir="Router"}
)

foreach ($project in $projects) {
    $outDir = Join-Path $OutputDir $project.Name
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Yellow
    dotnet publish $project.Path `
        -c $Configuration `
        -r $Runtime `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -p:FileVersion=$Version `
        -p:InformationalVersion=$Version `
        -o $outDir

    if ($LASTEXITCODE -ne 0) { throw "Failed to publish $($project.Name)" }

    $targetDir = Join-Path $bundleDir $project.Subdir
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Get-ChildItem $outDir -File | Copy-Item -Destination $targetDir -Force

    # If Controller subdir, ensure KioskBrowser.exe is co-located for easy start
    if ($project.Subdir -eq 'Tray' -or $project.Subdir -eq 'Controller') {
        $displayOut = Join-Path $OutputDir 'KioskBrowser'
        $browserExe = Get-ChildItem -Path $displayOut -Recurse -Filter 'KioskBrowser.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($browserExe) {
            Copy-Item $browserExe.FullName -Destination $targetDir -Force -ErrorAction SilentlyContinue
        }
    }
}

# Copy icons folder (optional runtime lookup)
$iconsSrc = Join-Path $root "icones"
if (Test-Path $iconsSrc) {
    Copy-Item $iconsSrc -Destination $bundleDir -Recurse -Force
    # Generate app.ico from kioskcontroller PNG if not present
    $icoPath = Join-Path $bundleDir "icones\app.ico"
    if (!(Test-Path $icoPath)) {
        $iconDir = Join-Path $bundleDir 'icones'
        $candidatePngs = @('kioskcontroller_256.png','kioskcontroller_128.png','kioskcontroller_64.png','kioskcontroller_32.png')
        $srcPng = $null
        foreach ($n in $candidatePngs) {
            $p = Join-Path $iconDir $n
            if (Test-Path $p) { $srcPng = $p; break }
        }
        if ($srcPng) {
            # Also duplicate the chosen PNG to a generic app_*.png name (keeps the original)
            try {
                $leaf = Split-Path -Leaf $srcPng
                if ($leaf -match 'kioskcontroller_(\d+)\.png') {
                    $size = $Matches[1]
                    $appPng = Join-Path $iconDir ("app_" + $size + ".png")
                    if (!(Test-Path $appPng)) { Copy-Item $srcPng $appPng -Force }
                    # Also create a simple app.png alias for convenience if not present
                    $appAlias = Join-Path $iconDir 'app.png'
                    if (!(Test-Path $appAlias)) { Copy-Item $srcPng $appAlias -Force }
                }
            } catch { }
            try {
                Add-Type -AssemblyName System.Drawing -ErrorAction Stop
                $bmp = [System.Drawing.Bitmap]::FromFile($srcPng)
                $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
                $fs = [System.IO.File]::Create($icoPath)
                $icon.Save($fs)
                $fs.Close()
                $icon.Dispose()
                $bmp.Dispose()
                Write-Host "Generated app.ico from $(Split-Path -Leaf $srcPng)" -ForegroundColor Green
            } catch {
                Write-Host "Failed to generate app.ico from PNG: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "No suitable kioskcontroller_*.png found to build app.ico" -ForegroundColor Yellow
        }
    }
}

# Add default config sample
$configDir = Join-Path $bundleDir "config"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$defaultConfig = Join-Path $root "config/default.config.json"
if (Test-Path $defaultConfig) {
    Copy-Item $defaultConfig -Destination (Join-Path $configDir "config.sample.json") -Force
}

# Top-level helper scripts
@(
    "run-both.ps1",
    "run-both-simple.ps1",
    "run-both.bat",
    "run-both.sh"
) | ForEach-Object {
    $p = Join-Path $root $_
    if (Test-Path $p) { Copy-Item $p -Destination $bundleDir -Force }
}

# README copy
$readme = Join-Path $root "README.md"
if (Test-Path $readme) { Copy-Item $readme -Destination (Join-Path $bundleDir "README.md") -Force }

Write-Host "Bundle created at: $bundleDir" -ForegroundColor Green

if ($Zip) {
    $zipPath = Join-Path $OutputDir ("NpsKiosk-Portable_" + (Get-Date -Format 'yyyyMMdd_HHmm') + ".zip")
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($bundleDir, $zipPath)
    Write-Host "ZIP created: $zipPath" -ForegroundColor Green
}

Write-Host "Done." -ForegroundColor Cyan


