<#
.SYNOPSIS
    Exports the Godot app to build/_artifacts/{version}/{rid}
.DESCRIPTION
    Uses GitVersion for semantic versioning. Pre-publishes the .NET project,
    then runs Godot --headless --export-release for each platform, and copies
    the published .NET assemblies into the Godot data directory
    (data_<appname>_<arch>) alongside the exported binaries.
.PARAMETER GodotPath
    Path to the Godot console executable.
.PARAMETER Platforms
    Comma-separated list of platforms to export. Valid: win-x64, linux-x64, osx-universal, all.
    Defaults to "all".
#>
param(
    [string]$GodotPath = "C:\lunar-horse\tools\Godot_v4.6.1-stable_mono_win64\Godot_v4.6.1-stable_mono_win64_console.exe",
    [string]$Platforms = "all"
)

$ErrorActionPreference = "Stop"
$repoRoot = (git rev-parse --show-toplevel).Trim()
$projectDir = Join-Path $repoRoot "project\hosts\complete-app"
$csproj = Join-Path $projectDir "complete-app.csproj"

# Application name from project.godot config/name (used for data directory naming)
$appName = "complete-app"

# Platform definitions: RID -> (preset name, binary name, Godot data dir suffix)
# Godot expects .NET assemblies in: data_<appname>_<arch>/ next to the binary
$platformMap = @{
    "win-x64"       = @{ Preset = "Windows Desktop"; Binary = "GiantIsopod.exe"; DataDir = "data_${appName}_x86_64" }
    "linux-x64"     = @{ Preset = "Linux";           Binary = "GiantIsopod.x86_64"; DataDir = "data_${appName}_linuxbsd_x86_64" }
    "osx-universal" = @{ Preset = "macOS";           Binary = "GiantIsopod.zip"; DataDir = "data_${appName}_universal" }
}

# Resolve platforms
$targetPlatforms = if ($Platforms -eq "all") {
    $platformMap.Keys
} else {
    $Platforms -split "," | ForEach-Object { $_.Trim() }
}

foreach ($p in $targetPlatforms) {
    if (-not $platformMap.ContainsKey($p)) {
        Write-Error "Unknown platform: $p. Valid: $($platformMap.Keys -join ', '), all"
        exit 1
    }
}

# --- GitVersion ---
Write-Host ":: Resolving version with GitVersion..." -ForegroundColor Cyan
$semver = (dotnet-gitversion /showvariable SemVer).Trim()
$fullSemver = (dotnet-gitversion /showvariable FullSemVer).Trim()
$informationalVersion = (dotnet-gitversion /showvariable InformationalVersion).Trim()

if ([string]::IsNullOrWhiteSpace($semver)) {
    Write-Error "GitVersion failed to produce a SemVer."
    exit 1
}

Write-Host "   SemVer:    $semver" -ForegroundColor Green
Write-Host "   Platforms: $($targetPlatforms -join ', ')" -ForegroundColor Green

# --- Pre-publish .NET ---
Write-Host ":: Publishing .NET project..." -ForegroundColor Cyan
dotnet publish $csproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$publishDir = Join-Path $projectDir ".godot\mono\temp\bin\Release\publish"

if (-not (Test-Path $GodotPath)) {
    Write-Error "Godot executable not found at: $GodotPath"
    exit 1
}

# --- Export each platform ---
$failed = @()

foreach ($rid in $targetPlatforms) {
    $info = $platformMap[$rid]
    $outDir = Join-Path $repoRoot "build\_artifacts\$semver\$rid"

    if (Test-Path $outDir) {
        Remove-Item -Recurse -Force $outDir
    }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $exportPath = Join-Path $outDir $info.Binary

    Write-Host ""
    Write-Host ":: Exporting [$rid] -> $($info.Preset)..." -ForegroundColor Cyan

    # Godot's internal dotnet publish may fail (known issue with external solution_directory)
    # but the PCK/exe export still succeeds. We pre-published .NET ourselves, so ignore Godot's build error.
    $prevPref = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $godotOutput = & $GodotPath --headless --path $projectDir --export-release $info.Preset $exportPath 2>&1
    $ErrorActionPreference = $prevPref

    # Check if the binary was produced
    $binaryExists = if ($rid -eq "osx-universal") {
        Test-Path $exportPath  # .zip file
    } else {
        Test-Path $exportPath
    }

    if (-not $binaryExists) {
        Write-Warning "  Export failed for $rid — no binary at $exportPath"
        $failed += $rid
        continue
    }

    # Copy .NET assemblies into the Godot data directory
    if (Test-Path $publishDir) {
        $dataDir = Join-Path $outDir $info.DataDir
        New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
        Copy-Item "$publishDir\*" $dataDir -Recurse -Force
        $dllCount = (Get-ChildItem $dataDir -Filter "*.dll").Count
        Write-Host "   Copied $dllCount DLLs -> $($info.DataDir)/" -ForegroundColor Green
    }

    # Write version.json
    @{
        SemVer               = $semver
        FullSemVer           = $fullSemver
        InformationalVersion = $informationalVersion
        Platform             = $rid
        Preset               = $info.Preset
        ExportedAt           = (Get-Date -Format "o")
    } | ConvertTo-Json | Set-Content (Join-Path $outDir "version.json") -Encoding UTF8

    Write-Host "   Done: $outDir" -ForegroundColor Green
}

# --- Summary ---
Write-Host ""
if ($failed.Count -gt 0) {
    Write-Warning ":: Export completed with failures: $($failed -join ', ')"
    exit 1
} else {
    Write-Host ":: All exports complete!" -ForegroundColor Green
    Write-Host "   Artifact root: build\_artifacts\$semver" -ForegroundColor Green
    Write-Host "   Version:       $semver" -ForegroundColor Green
}
