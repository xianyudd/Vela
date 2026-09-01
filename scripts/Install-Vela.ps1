<#
.SYNOPSIS
    Publishes the single-file Vela.exe and installs it into the daily-use directory.

.DESCRIPTION
    The publish profile overrides AssemblyName from Vela.Tui to Vela, so a profile
    publish yields one self-contained Vela.exe while a plain build yields
    Vela.Tui.exe plus its dependency DLLs. Copying both shapes into the same
    install directory leaves two runnable entry points that disagree about which
    code they carry, and the manifest requests requireAdministrator, so the stale
    one still runs the elevated diskpart flow.

    This script makes the install directory hold exactly one entry point:
    Vela.exe from the current source tree. Framework-dependent leftovers are
    reported and removed. Delivery files named by docs/testing-and-release.md --
    README.md and logs-link.txt -- are preserved.

.PARAMETER Destination
    The install directory. Defaults to D:\DevTools\Vela.

.PARAMETER SkipTests
    Skips the test run. Use only when the suite was already verified in this
    working tree.

.PARAMETER Force
    Removes conflicting entry points without prompting.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Install-Vela.ps1

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Install-Vela.ps1 -SkipTests -Force
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Destination = 'D:\DevTools\Vela',
    [switch]$SkipTests,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'Vela.sln'
$tuiProject = Join-Path $repoRoot 'src\Vela.Tui\Vela.Tui.csproj'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish\win-x64'
$publishedExecutable = Join-Path $publishDirectory 'Vela.exe'

# Files the delivery layout expects to survive an install.
$preservedNames = @('README.md', 'logs-link.txt')

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "> dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found. Install the .NET 10 SDK and retry.'
}

Write-Host '== Restore =='
Invoke-Dotnet @(
    'restore', $solution,
    '-r', 'win-x64',
    '--locked-mode',
    '--ignore-failed-sources',
    '-p:EnableRuntimePackDownload=false',
    '-p:DisableTransitiveFrameworkReferenceDownloads=true'
)

Write-Host '== Build Release =='
Invoke-Dotnet @('build', $solution, '-c', 'Release', '--no-restore', '--nologo')

if ($SkipTests) {
    Write-Warning 'Tests skipped by request.'
}
else {
    Write-Host '== Test =='
    Invoke-Dotnet @('test', $solution, '-c', 'Release', '--no-build', '--no-restore', '--nologo')
}

# A stale publish directory would let a failed publish pass the existence check
# below and install an older executable.
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Write-Host '== Publish single-file =='
Invoke-Dotnet @(
    'publish', $tuiProject,
    '-c', 'Release',
    '--no-restore',
    '-p:PublishProfile=win-x64-singlefile',
    '-o', $publishDirectory
)

if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Publish finished but the executable is missing: $publishedExecutable"
}

$published = Get-Item -LiteralPath $publishedExecutable
$publishedHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
Write-Host ''
Write-Host ('Published {0} ({1:N0} bytes)' -f $published.FullName, $published.Length)
Write-Host ("SHA256    {0}" -f $publishedHash)

Write-Host ''
Write-Host '== Install =='
if (-not (Test-Path -LiteralPath $Destination)) {
    if ($PSCmdlet.ShouldProcess($Destination, 'Create install directory')) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
}

$destinationExecutable = Join-Path $Destination 'Vela.exe'

# Everything that is neither the single-file entry point nor a delivery file is a
# framework-dependent leftover from a non-profile publish.
$conflicts = @()
if (Test-Path -LiteralPath $Destination) {
    $conflicts = Get-ChildItem -LiteralPath $Destination -Force |
        Where-Object {
            $_.Name -ne 'Vela.exe' -and $preservedNames -notcontains $_.Name
        }
}

if ($conflicts.Count -gt 0) {
    Write-Host ''
    Write-Host "Conflicting entry points and dependencies in ${Destination}:"
    foreach ($conflict in $conflicts) {
        $suffix = if ($conflict.PSIsContainer) { '\' } else { '' }
        Write-Host ("  {0}{1}" -f $conflict.Name, $suffix)
    }
    Write-Host ''
    Write-Host 'These come from a plain build (Vela.Tui.exe plus dependencies) and'
    Write-Host 'are not part of the single-file layout.'

    if (-not $Force) {
        $answer = Read-Host 'Remove them? Type YES to continue'
        if ($answer -cne 'YES') {
            throw 'Install cancelled; the destination was left unchanged.'
        }
    }

    foreach ($conflict in $conflicts) {
        if ($PSCmdlet.ShouldProcess($conflict.FullName, 'Remove conflicting install file')) {
            Remove-Item -LiteralPath $conflict.FullName -Recurse -Force
        }
    }
}

if ($PSCmdlet.ShouldProcess($destinationExecutable, 'Install Vela.exe')) {
    try {
        Copy-Item -LiteralPath $publishedExecutable -Destination $destinationExecutable -Force
    }
    catch [System.IO.IOException] {
        throw "Could not write ${destinationExecutable}. Close any running Vela instance and retry. $($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host '== Installed =='
$installed = Get-Item -LiteralPath $destinationExecutable
$installedHash = (Get-FileHash -LiteralPath $destinationExecutable -Algorithm SHA256).Hash
Write-Host ('Path      {0}' -f $installed.FullName)
Write-Host ('Size      {0:N0} bytes' -f $installed.Length)
Write-Host ('Modified  {0:yyyy-MM-dd HH:mm:ss}' -f $installed.LastWriteTime)
Write-Host ("SHA256    {0}" -f $installedHash)

if ($installedHash -ne $publishedHash) {
    throw 'The installed executable does not match the published one.'
}

Write-Host ''
Write-Host "Contents of ${Destination}:"
Get-ChildItem -LiteralPath $Destination -Force |
    Sort-Object PSIsContainer, Name |
    ForEach-Object {
        $suffix = if ($_.PSIsContainer) { '\' } else { '' }
        Write-Host ('  {0}{1}' -f $_.Name, $suffix)
    }

Write-Host ''
Write-Host 'Vela.exe is the only entry point. Launch it from Windows Terminal or a shortcut.'
