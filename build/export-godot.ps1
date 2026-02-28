<#
.SYNOPSIS
    Exports the Godot app to build/_artifacts/{version}
.DESCRIPTION
    Uses GitVersion for semantic versioning. Pre-publishes the .NET project,
    then runs Godot --headless --export-release for the PCK/exe, and finally
    copies the published .NET assemblies alongside the exported executable.
.PARAMETER GodotPath
    Path to the Godot console executable.
#>
param(
    [string]$GodotPath = "C:\lunar-horse\tools\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe"
)

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel).Trim()
$projectDir = Join-Path $repoRoot "project\hosts\complete-app"
$csproj = Join-Path $projectDir "complete-app.csproj"

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

# --- Pre-publish .NET ---
Write-Host ":: Publishing .NET project..." -ForegroundColor Cyan
$publishDir = Join-Path $projectDir ".godot\mono\temp\bin\Release\publish"
dotnet publish $csproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# --- Godot export (PCK + exe) ---
Write-Host ":: Exporting Godot project to $exportPath ..." -ForegroundColor Cyan

if (-not (Test-Path $GodotPath)) {
    Write-Error "Godot executable not found at: $GodotPath"
    exit 1
}

& $GodotPath --headless --path $projectDir --export-release "Windows Desktop" $exportPath

# Godot may return warnings (exit code 0 with WARNING output) — check exe exists
if (-not (Test-Path $exportPath)) {
    Write-Error "Godot export failed — no exe produced at $exportPath"
    exit 1
}

# --- Copy .NET assemblies alongside exe ---
Write-Host ":: Copying .NET assemblies..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Copy-Item "$publishDir\*" $artifactDir -Recurse -Force
    Write-Host "   Copied $(( Get-ChildItem $artifactDir -Filter '*.dll' ).Count) DLLs" -ForegroundColor Green
} else {
    Write-Warning "Publish directory not found at $publishDir — .NET assemblies may be missing"
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
