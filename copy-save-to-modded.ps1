<#
.SYNOPSIS
    Copies an unmodded Slay the Spire 2 profile's save data into its modded profile.

.DESCRIPTION
    Discovers the selected STS2 account/profile and copies its progress.save into
    the corresponding modded profile. Use -AllSaveFiles to include active runs,
    preferences, run history, backups, and any other files beneath the unmodded
    saves directory. Destination-only files are preserved.

    Only new or changed files are copied. When replacing an existing modded file,
    its original modification time is retained so STS2's timestamp-based Steam
    Cloud sync does not immediately restore stale contents.

    Before a real copy, an existing modded saves directory is backed up beneath
    <SaveRoot>/save-copy-backups. Run this script while STS2 is closed.

.PARAMETER DryRun
    Reports the account, paths, backup, and file operations without changing disk.

.PARAMETER AllSaveFiles
    Copies every file beneath the unmodded saves directory instead of only
    progress.save. Steam Cloud may remove copied files that did not already exist
    in the modded cloud profile; the script warns when this applies.

.PARAMETER AccountId
    Platform account directory to use (for Steam, the numeric Steam account ID).
    When omitted, the script succeeds only if exactly one account contains the
    requested unmodded profile.

.PARAMETER ProfileId
    STS2 profile number to copy. Defaults to 1.

.PARAMETER SaveRoot
    STS2 user-data root. On Windows this defaults to
    %APPDATA%/SlayTheSpire2.

.PARAMETER PlatformDirectory
    Platform directory beneath SaveRoot. Defaults to steam.

.PARAMETER BackupRoot
    Directory in which existing modded saves are backed up. Defaults to
    <SaveRoot>/save-copy-backups.

.PARAMETER Force
    Allows a real copy even when an STS2-looking process is running. This does not
    change overwrite behavior or disable backups.

.EXAMPLE
    ./copy-save-to-modded.ps1 -DryRun

.EXAMPLE
    ./copy-save-to-modded.ps1 -AccountId 76561198000000000 -ProfileId 2
#>
[CmdletBinding()]
param(
    [switch]$DryRun,

    [switch]$AllSaveFiles,

    [ValidatePattern('^[^\\/]+$')]
    [string]$AccountId,

    [ValidateRange(1, 99)]
    [int]$ProfileId = 1,

    [string]$SaveRoot = (Join-Path $env:APPDATA "SlayTheSpire2"),

    [ValidatePattern('^[^\\/]+$')]
    [string]$PlatformDirectory = "steam",

    [string]$BackupRoot,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Join-PathParts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Base,

        [Parameter(Mandatory = $true)]
        [string[]]$Parts
    )

    $result = $Base
    foreach ($part in $Parts) {
        $result = Join-Path $result $part
    }
    return $result
}

function Get-RelativeChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $prefix = $Parent.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $Child.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$Child' is not beneath '$Parent'."
    }

    return $Child.Substring($prefix.Length)
}

if (-not (Test-Path -LiteralPath $SaveRoot -PathType Container)) {
    throw "STS2 save root does not exist: '$SaveRoot'. Use -SaveRoot to specify it."
}

$SaveRoot = (Resolve-Path -LiteralPath $SaveRoot).Path
$platformRoot = Join-Path $SaveRoot $PlatformDirectory
if (-not (Test-Path -LiteralPath $platformRoot -PathType Container)) {
    throw "STS2 platform save directory does not exist: '$platformRoot'."
}

$profileDirectoryName = "profile$ProfileId"
if (-not $AccountId) {
    $candidateAccounts = @(
        Get-ChildItem -LiteralPath $platformRoot -Directory -Force |
            Where-Object {
                Test-Path -LiteralPath (
                    Join-PathParts $_.FullName @($profileDirectoryName, "saves")
                ) -PathType Container
            }
    )

    if ($candidateAccounts.Count -eq 0) {
        throw "No account beneath '$platformRoot' has unmodded $profileDirectoryName save data."
    }
    if ($candidateAccounts.Count -gt 1) {
        $candidateNames = ($candidateAccounts.Name | Sort-Object) -join ", "
        throw "Multiple accounts have unmodded $profileDirectoryName save data ($candidateNames). Select one with -AccountId."
    }

    $AccountId = $candidateAccounts[0].Name
}

$accountRoot = Join-Path $platformRoot $AccountId
$sourceSaves = Join-PathParts $accountRoot @($profileDirectoryName, "saves")
$targetSaves = Join-PathParts $accountRoot @("modded", $profileDirectoryName, "saves")

if (-not (Test-Path -LiteralPath $sourceSaves -PathType Container)) {
    throw "Unmodded save directory does not exist: '$sourceSaves'."
}

$sourceSaves = (Resolve-Path -LiteralPath $sourceSaves).Path
if ((Test-Path -LiteralPath $targetSaves) -and
    -not (Test-Path -LiteralPath $targetSaves -PathType Container)) {
    throw "Modded save path exists but is not a directory: '$targetSaves'."
}
if (Test-Path -LiteralPath $targetSaves -PathType Container) {
    $targetSaves = (Resolve-Path -LiteralPath $targetSaves).Path
}
else {
    $targetSaves = [System.IO.Path]::GetFullPath($targetSaves)
}

if ($sourceSaves.Equals($targetSaves, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Source and target save directories resolve to the same path."
}

$sourceFiles = @(
    if ($AllSaveFiles) {
        Get-ChildItem -LiteralPath $sourceSaves -File -Recurse -Force |
            Sort-Object FullName
    }
    else {
        $progressPath = Join-Path $sourceSaves "progress.save"
        if (-not (Test-Path -LiteralPath $progressPath -PathType Leaf)) {
            throw "Unmodded progress save does not exist: '$progressPath'."
        }
        Get-Item -LiteralPath $progressPath -Force
    }
)
if ($sourceFiles.Count -eq 0) {
    throw "Unmodded save directory contains no files: '$sourceSaves'."
}

$sourceReparsePoints = @(
    $sourceFiles |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }
)
if ($sourceReparsePoints.Count -gt 0) {
    throw "The source contains a reparse point, which this script will not follow: '$($sourceReparsePoints[0].FullName)'."
}

$changes = [System.Collections.Generic.List[object]]::new()
foreach ($sourceFile in $sourceFiles) {
    $relativePath = Get-RelativeChildPath -Parent $sourceSaves -Child $sourceFile.FullName
    $targetFile = Join-Path $targetSaves $relativePath
    if ((Test-Path -LiteralPath $targetFile) -and
        -not (Test-Path -LiteralPath $targetFile -PathType Leaf)) {
        throw "A target path that should be a file is another filesystem object: '$targetFile'."
    }
    $targetExists = Test-Path -LiteralPath $targetFile -PathType Leaf
    $isUnchanged = $false
    $targetLastWriteTimeUtc = $null

    if ($targetExists) {
        $targetInfo = Get-Item -LiteralPath $targetFile -Force
        $targetLastWriteTimeUtc = $targetInfo.LastWriteTimeUtc
        if ($sourceFile.Length -eq $targetInfo.Length) {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
            $isUnchanged = $sourceHash -eq $targetHash
        }
    }

    if (-not $isUnchanged) {
        $changes.Add([pscustomobject]@{
            Action = if ($targetExists) { "OVERWRITE" } else { "CREATE" }
            RelativePath = $relativePath
            Source = $sourceFile.FullName
            Target = $targetFile
            TargetLastWriteTimeUtc = $targetLastWriteTimeUtc
        })
    }
}

Write-Host "STS2 unmodded-to-modded save copy"
Write-Host "Account: $AccountId"
Write-Host "Profile: $ProfileId"
Write-Host "Scope:   $(if ($AllSaveFiles) { 'all save files' } else { 'progress.save' })"
Write-Host "Source:  $sourceSaves"
Write-Host "Target:  $targetSaves"

if ($changes.Count -eq 0) {
    Write-Host "The modded save data is already up to date." -ForegroundColor Green
    return
}

if (-not $BackupRoot) {
    $BackupRoot = Join-Path $SaveRoot "save-copy-backups"
}
else {
    $BackupRoot = [System.IO.Path]::GetFullPath($BackupRoot)
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$backupSaves = Join-PathParts $BackupRoot @($PlatformDirectory, $AccountId, $profileDirectoryName, $timestamp, "saves")
$targetExists = Test-Path -LiteralPath $targetSaves -PathType Container

if ($DryRun) {
    Write-Host "DRY RUN: no files or directories will be changed." -ForegroundColor Yellow
    if ($targetExists) {
        Write-Host "BACKUP    $targetSaves"
        Write-Host "       -> $backupSaves"
    }
    foreach ($change in $changes) {
        Write-Host ("{0,-9} {1}" -f $change.Action, $change.RelativePath)
    }
    if (@($changes | Where-Object { $_.Action -eq "OVERWRITE" }).Count -gt 0) {
        Write-Host "Existing target timestamps would be retained for STS2 Steam Cloud compatibility."
    }
    Write-Host "Would copy $($changes.Count) of $($sourceFiles.Count) source file(s)."
    return
}

if (-not $Force) {
    $runningGame = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like "*Slay*Spire*" }
    )
    if ($runningGame.Count -gt 0) {
        $processNames = ($runningGame.ProcessName | Sort-Object -Unique) -join ", "
        throw "STS2 may be running ($processNames). Close it before copying saves, or use -Force to bypass this check."
    }
}

if ($targetExists) {
    $backupParent = Split-Path -Parent $backupSaves
    New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
    Copy-Item -LiteralPath $targetSaves -Destination $backupSaves -Recurse -Force
    Write-Host "Backed up the existing modded saves to '$backupSaves'."
}

foreach ($change in $changes) {
    $targetDirectory = Split-Path -Parent $change.Target
    if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    }
    Copy-Item -LiteralPath $change.Source -Destination $change.Target -Force
    if ($null -ne $change.TargetLastWriteTimeUtc) {
        [System.IO.File]::SetLastWriteTimeUtc($change.Target, $change.TargetLastWriteTimeUtc)
    }
    Write-Host ("{0,-9} {1}" -f $change.Action, $change.RelativePath)
}

Write-Host "Copied $($changes.Count) of $($sourceFiles.Count) source file(s) into the modded profile." -ForegroundColor Green
$createdFiles = @($changes | Where-Object { $_.Action -eq "CREATE" })
if ($createdFiles.Count -gt 0) {
    Write-Warning (
        "Steam Cloud may remove $($createdFiles.Count) newly created modded file(s) that have no cloud counterpart. " +
        "This can affect a copied current_run.save or new history entries. Disable Steam Cloud for STS2 before " +
        "launching if those source-only files must be retained."
    )
}
