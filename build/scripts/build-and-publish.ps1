param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "build/publish",
    [string]$InnoSetupPath
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csproj = Join-Path $root "src/KioskController/KioskController.csproj"

if (!(Test-Path $csproj)) {
    throw "Project not found: $csproj"
}

[xml]$projeto = Get-Content $csproj -Raw
$Version = $projeto.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1

if (-not $Version) {
    throw "Version not found in $csproj"
}

$Version = $Version.Trim()

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  KioskMax Build and Publish $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

& (Join-Path $PSScriptRoot "publish-bundle.ps1") -Configuration $Configuration -Runtime $Runtime -OutputDir $OutputDir -Version $Version -Zip

if (-not $InnoSetupPath) {
    $candidatos = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $InnoSetupPath = $candidatos | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if (-not $InnoSetupPath -or !(Test-Path $InnoSetupPath)) {
    Write-Host "[AVISO] Inno Setup 6 (ISCC.exe) nao encontrado; instalador nao gerado." -ForegroundColor Yellow
} else {
    $iss = Join-Path $PSScriptRoot "installer.iss"
    Write-Host "Compilando instalador com $InnoSetupPath..." -ForegroundColor Yellow
    & $InnoSetupPath "/DMyAppVersion=$Version" $iss

    if ($LASTEXITCODE -ne 0) {
        throw "ISCC falhou (codigo $LASTEXITCODE)"
    }

    Write-Host "[OK] Instalador gerado em $(Join-Path $root 'build/publish/installer')" -ForegroundColor Green
}

Write-Host ""
Write-Host "[OK] Versao $Version publicada em $(Join-Path $root $OutputDir)" -ForegroundColor Green
