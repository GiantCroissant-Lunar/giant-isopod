<#
.SYNOPSIS
    Exports the Godot app to build/_artifacts/{version}/{rid}
.DESCRIPTION
    Uses GitVersion for semantic versioning. Pre-publishes the .NET project,
    then runs Godot --headless --export-release for each platform, and copies
    the published .NET assemblies into the Godot data directory
    (data_<AssemblyName>_<platform>_<arch>) alongside the exported binaries.
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

# Assembly name from csproj (Godot uses this for data directory, not config/name)
$assemblyName = "GiantIsopod"

# Platform definitions: RID -> (preset name, binary name, Godot data dir suffix)
# Godot looks for assemblies in: data_<AssemblyName>_<platform>_<arch>/ next to the binary
# Platform names from godotsharp_dirs.cpp: windows, linuxbsd, macos
$platformMap = @{
    "win-x64"       = @{ Preset = "Windows Desktop"; Binary = "GiantIsopod.exe"; DataDir = "data_${assemblyName}_windows_x86_64"; Rid = "win-x64" }
    "linux-x64"     = @{ Preset = "Linux";           Binary = "GiantIsopod.x86_64"; DataDir = "data_${assemblyName}_linuxbsd_x86_64"; Rid = "linux-x64" }
    "osx-universal" = @{ Preset = "macOS";           Binary = "GiantIsopod.zip"; DataDir = "data_${assemblyName}_macos_universal"; Rid = "osx-x64" }
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

# --- Pre-publish .NET (per-platform, self-contained) ---
# Godot exported binaries require hostfxr/coreclr in the data directory.
# Self-contained publish bundles the .NET runtime alongside the assemblies.
$publishDirs = @{}

foreach ($rid in $targetPlatforms) {
    $info = $platformMap[$rid]
    $publishOut = Join-Path $projectDir ".godot\mono\temp\bin\Release\publish-$rid"
    Write-Host ":: Publishing .NET for $rid (self-contained)..." -ForegroundColor Cyan
    dotnet publish $csproj -c Release -r $info.Rid --self-contained --nologo -v quiet -o $publishOut
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $rid with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    $publishDirs[$rid] = $publishOut
}

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
    $publishDir = $publishDirs[$rid]
    if ($publishDir -and (Test-Path $publishDir)) {
        if ($rid -eq "osx-universal") {
            # macOS: inject data dir into .app bundle inside the zip
            $tempExtract = Join-Path $env:TEMP "godot-mac-inject-$([guid]::NewGuid().ToString('N'))"
            Expand-Archive -Path $exportPath -DestinationPath $tempExtract -Force
            $appBundle = Get-ChildItem $tempExtract -Filter "*.app" -Directory | Select-Object -First 1
            $resourcesDir = Join-Path $appBundle.FullName "Contents\Resources\$($info.DataDir)"
            New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
            Copy-Item "$publishDir\*" $resourcesDir -Recurse -Force
            $dllCount = (Get-ChildItem $resourcesDir -Filter "*.dll").Count
            # Re-zip the .app bundle
            Remove-Item $exportPath -Force
            Compress-Archive -Path "$tempExtract\*" -DestinationPath $exportPath -CompressionLevel Optimal
            Remove-Item -Recurse -Force $tempExtract
            Write-Host "   Injected $dllCount DLLs -> $($appBundle.Name)/Contents/Resources/$($info.DataDir)/" -ForegroundColor Green
        } else {
            # Windows/Linux: data dir sits next to the binary
            $dataDir = Join-Path $outDir $info.DataDir
            New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
            Copy-Item "$publishDir\*" $dataDir -Recurse -Force
            $dllCount = (Get-ChildItem $dataDir -Filter "*.dll").Count
            Write-Host "   Copied $dllCount DLLs -> $($info.DataDir)/" -ForegroundColor Green
        }
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

    # Write latest pointer for easy launching
    $artifactsRoot = Join-Path $repoRoot "build\_artifacts"
    $latestFile = Join-Path $artifactsRoot "latest.txt"
    $semver | Set-Content $latestFile -Encoding UTF8 -NoNewline
    Write-Host "   Latest:        $latestFile -> $semver" -ForegroundColor Green
}
