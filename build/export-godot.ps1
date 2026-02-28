<#
.SYNOPSIS
    Exports the Godot app to build/_artifacts/{version}
.DESCRIPTION
    Uses GitVersion for semantic versioning and Godot --headless --export-release
    to produce a Windows x86_64 build.
.PARAMETER GodotPath
    Path to the Godot console executable. Defaults to the project standard location.
#>
param(
    [string]$GodotPath = "C:\lunar-horse\tools\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe"
)

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel).Trim()
$projectDir = Join-Path $repoRoot "project\hosts\complete-app"

# --- GitVersion ---
Write-Host ":: Resolving version with GitVersion..." -ForegroundColor Cyan
$semver = (dotnet-gitversion /showvariable SemVer).Trim()
$fullSemver = (dotnet-gitversion /showvariable FullSemVer).Trim()
$informationalVersion = (dotnet-gitversion /showvariable InformationalVersion).Trim()

if ([string]::IsNullOrWhiteSpace($semver)) {
    Write-Error "GitVersion failed to produce a SemVer."
    exit 1
}

Write-Host "   SemVer:  $semver" -ForegroundColor Green
Write-Host "   Full:    $fullSemver" -ForegroundColor Green
Write-Host "   Info:    $informationalVersion" -ForegroundColor Green

# --- Output path ---
$artifactDir = Join-Path $repoRoot "build\_artifacts\$semver"
if (Test-Path $artifactDir) {
    Write-Host ":: Cleaning existing artifact directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $artifactDir
}
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$exportPath = Join-Path $artifactDir "GiantIsopod.exe"

# --- Build .NET first ---
Write-Host ":: Building .NET solution..." -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot "project\GiantIsopod.sln") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# --- Godot export ---
Write-Host ":: Exporting Godot project to $exportPath ..." -ForegroundColor Cyan

if (-not (Test-Path $GodotPath)) {
    Write-Error "Godot executable not found at: $GodotPath"
    exit 1
}

& $GodotPath --headless --path $projectDir --export-release "Windows Desktop" $exportPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "Godot export failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# --- Write version file ---
@{
    SemVer               = $semver
    FullSemVer           = $fullSemver
    InformationalVersion = $informationalVersion
    ExportedAt           = (Get-Date -Format "o")
} | ConvertTo-Json | Set-Content (Join-Path $artifactDir "version.json") -Encoding UTF8

Write-Host ""
Write-Host ":: Export complete!" -ForegroundColor Green
Write-Host "   Artifact: $artifactDir" -ForegroundColor Green
Write-Host "   Version:  $semver" -ForegroundColor Green
