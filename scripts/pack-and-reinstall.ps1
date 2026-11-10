<#
.SYNOPSIS
    Packs the RoslynPad.Cli tool and reinstalls it globally.

.DESCRIPTION
    Builds and packs the RoslynPad.Cli project into the local ./nupkg folder,
    then uninstalls any existing global 'roslynpad.cli' tool and reinstalls
    the freshly packed version from the local package source.

    Because the package version is fixed (0.0.1), the tool must be uninstalled
    before it can be reinstalled - `dotnet tool install` will not overwrite an
    existing version.

.PARAMETER Configuration
    The build configuration to use. Defaults to 'Release'.

.PARAMETER SkipPack
    If set, skips the pack step and only reinstalls from the existing nupkg.

.EXAMPLE
    ./scripts/pack-and-reinstall.ps1

.EXAMPLE
    ./scripts/pack-and-reinstall.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPack
)

$ErrorActionPreference = 'Stop'

# Resolve the repo root (parent of the scripts folder).
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CliProject = Join-Path $RepoRoot 'src\RoslynPad.Cli\RoslynPad.Cli.csproj'
$NupkgDir   = Join-Path $RepoRoot 'nupkg'
$PackageId  = 'roslynpad.cli'

if (-not $SkipPack) {
    Write-Host "==> Packing $CliProject ($Configuration)..." -ForegroundColor Cyan
    dotnet pack $CliProject -c $Configuration -o $NupkgDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host '==> Skipping pack step (-SkipPack).' -ForegroundColor Yellow
}

# Find the newest nupkg for the package so we can report the version being installed.
$Nupkg = Get-ChildItem -Path $NupkgDir -Filter "$PackageId*.nupkg" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $Nupkg) {
    throw "No nupkg found for '$PackageId' in '$NupkgDir'. Run without -SkipPack first."
}

$Version = $Nupkg.BaseName -replace "^$PackageId\.", ''
Write-Host "==> Installing $PackageId version $Version from $($Nupkg.FullName)" -ForegroundColor Cyan

# Uninstall any existing version (required since the version is fixed).
$Installed = dotnet tool list --global | Select-String -Pattern "^$PackageId\s"
if ($Installed) {
    Write-Host "==> Uninstalling existing $PackageId..." -ForegroundColor Yellow
    dotnet tool uninstall --global $PackageId
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool uninstall failed with exit code $LASTEXITCODE"
    }
}

# Install the freshly packed version from the local source (configured in nuget.config).
Write-Host "==> Installing $PackageId $Version..." -ForegroundColor Cyan
dotnet tool install --global $PackageId --version $Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool install failed with exit code $LASTEXITCODE"
}

Write-Host "==> Verifying installation..." -ForegroundColor Cyan
roslynpad --version
if ($LASTEXITCODE -ne 0) {
    throw "roslynpad --version failed with exit code $LASTEXITCODE"
}

Write-Host "==> Done. $PackageId $Version installed successfully." -ForegroundColor Green
