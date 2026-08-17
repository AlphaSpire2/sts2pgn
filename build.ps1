<#
.SYNOPSIS
    Builds the STS2PGN mod and its portable core library.

.PARAMETER GameDir
    Path to the Slay the Spire 2 installation root. Falls back to STS2_GAME_DIR.

.PARAMETER GameDataDir
    Direct path to the platform-specific game data directory. Use this to
    override platform detection.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.
#>
param(
    [string]$GameDir,
    [string]$GameDataDir,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if (-not $GameDir) {
    $GameDir = $env:STS2_GAME_DIR
}

if (-not $GameDataDir) {
    if (-not $GameDir) {
        Write-Error "Specify -GameDir, -GameDataDir, or set STS2_GAME_DIR."
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $GameDataDir = Join-Path $GameDir "data_sts2_windows_x86_64"
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        $GameDataDir = Join-Path $GameDir "SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
    }
    else {
        $GameDataDir = Join-Path $GameDir "data_sts2_linuxbsd_x86_64"
    }
}

foreach ($assembly in @("sts2.dll", "GodotSharp.dll", "0Harmony.dll")) {
    $assemblyPath = Join-Path $GameDataDir $assembly
    if (-not (Test-Path $assemblyPath)) {
        Write-Error "Could not find $assembly at '$assemblyPath'."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET 9 SDK is required but 'dotnet' was not found."
}

$project = Join-Path $PSScriptRoot "src/STS2PGN.Mod/STS2PGN.Mod.csproj"
$output = Join-Path $PSScriptRoot "out/STS2_PGN"

dotnet build $project `
    --configuration $Configuration `
    --output $output `
    -p:STS2GameDataDir="$GameDataDir"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built STS2PGN in '$output'." -ForegroundColor Green
Write-Host "Copy STS2_PGN.dll, STS2PGN.Core.dll, STS2_PGN.json, and config.json to the game's mods directory."
