param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build/publish",
    [string]$Version = "1.3.0",
    [string]$InnoSetupPath
)

$ErrorActionPreference = "Stop"

# 1) Publish portable bundle first
powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\\publish-bundle.ps1" -Configuration $Configuration -Runtime $Runtime -Version $Version

# 2) Compile installer using Inno Setup
if (-not $InnoSetupPath) {
    $candidates = @(
        "C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe",
        "C:\\Program Files\\Inno Setup 6\\ISCC.exe"
    )
    $InnoSetupPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $InnoSetupPath -or !(Test-Path $InnoSetupPath)) {
    throw "Inno Setup not found. Set -InnoSetupPath or install Inno Setup 6."
}

$iss = Join-Path $PSScriptRoot "installer.iss"
if (!(Test-Path $iss)) {
    throw "installer.iss not found: $iss"
}

# Pass version via preprocessor define
& "$InnoSetupPath" "/DMyAppVersion=$Version" "$iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Write-Host "Installer built. Check: $OutputDir/installer" -ForegroundColor Green
