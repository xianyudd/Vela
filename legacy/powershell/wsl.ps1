#requires -Version 7.0
<#!
.SYNOPSIS
    Shuts down a WSL distribution and compacts its VHDX with detailed diagnostics.

.DESCRIPTION
    The script validates the Distro/VHDX pairing, records a detailed text log and
    JSON summary, waits for WSL to stop, then invokes DiskPart's `compact vdisk`.
    `-WhatIf` performs all read-only preflight checks without shutting down WSL
    or invoking DiskPart.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
  [Parameter(Mandatory = $false)]
  [string]$Distro = 'Ubuntu-24.04',

  [Parameter(Mandatory = $false)]
  [string]$Vhdx = 'D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx',

  [Parameter(Mandatory = $false)]
  [ValidateSet('Global', 'Distro')]
  [string]$ShutdownMode = 'Global',

  [Parameter(Mandatory = $false)]
  [switch]$Force,

  [Parameter(Mandatory = $false)]
  [string]$LogDir,

  [Parameter(Mandatory = $false)]
  [switch]$NoLog,

  [Parameter(Mandatory = $false)]
  [ValidateRange(5, 300)]
  [int]$ShutdownTimeoutSeconds = 45,

  [Parameter(Mandatory = $false)]
  [switch]$AllowVhdxMismatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$script:RunStarted = Get-Date
$script:LogPath = $null
$script:SummaryPath = $null
$script:BeforeSnapshot = $null
$script:AfterSnapshot = $null
$script:CommandHistory = [System.Collections.Generic.List[object]]::new()
$script:ErrorText = $null
$script:ExitCode = 1

function Format-Size {
  param([Parameter(Mandatory)][Int64]$Bytes)

  $units = @('B', 'KiB', 'MiB', 'GiB', 'TiB')
  [double]$value = $Bytes
  $unitIndex = 0
  while ($value -ge 1024 -and $unitIndex -lt ($units.Count - 1)) {
    $value /= 1024
    $unitIndex++
  }

  '{0:N2} {1} ({2:N0} bytes)' -f $value, $units[$unitIndex], $Bytes
}

function Get-ScriptRootPath {
  if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    return $PSScriptRoot
  }

  $scriptPath = $PSCommandPath ?? $MyInvocation.MyCommand.Path
  if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    throw 'Unable to determine the script directory.'
  }

  Split-Path -Parent $scriptPath
}

function Resolve-AbsolutePath {
  param([Parameter(Mandatory)][string]$Path)

  if ([System.IO.Path]::IsPathRooted($Path)) {
    return [System.IO.Path]::GetFullPath($Path)
  }

  [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Write-Log {
  param(
    [Parameter(Mandatory)][string]$Message,
    [ValidateSet('INFO', 'WARN', 'ERROR', 'DEBUG', 'CMD')]
    [string]$Level = 'INFO'
  )

  $timestamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffK')
  $line = "$timestamp [$Level] $Message"
  $color = switch ($Level) {
    'ERROR' { 'Red' }
    'WARN'  { 'Yellow' }
    'CMD'   { 'Cyan' }
    'DEBUG' { 'DarkGray' }
    default { 'Gray' }
  }

  Write-Host $line -ForegroundColor $color
  if ($script:LogPath) {
    try {
      # Use .NET I/O so a WhatIf preflight still leaves an auditable diagnostic log.
      [System.IO.File]::AppendAllText(
        $script:LogPath,
        $line + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
      )
    } catch {
      Write-Host "$(Get-Date -Format o) [WARN] Could not write to log: $($_.Exception.Message)" -ForegroundColor Yellow
    }
  }
}

function Start-RunLogging {
  if ($NoLog) { return }

  if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path (Get-ScriptRootPath) 'logs'
  }

  $LogDir = Resolve-AbsolutePath $LogDir
  # New-Item/Set-Content honor the global WhatIf preference. Logs remain useful
  # during WhatIf, so use direct .NET file I/O for this diagnostic-only output.
  [System.IO.Directory]::CreateDirectory($LogDir) | Out-Null

  $stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
  $script:LogPath = Join-Path $LogDir "wsl-compact-$stamp.log"
  $script:SummaryPath = Join-Path $LogDir "wsl-compact-$stamp.summary.json"
  [System.IO.File]::WriteAllText(
    $script:LogPath,
    "# WSL2 VHDX Compact diagnostic log$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false)
  )
  Write-Log "Detailed log: $script:LogPath"
  Write-Log "JSON summary: $script:SummaryPath"
}

function Test-IsAdministrator {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = [Security.Principal.WindowsPrincipal]::new($identity)
  $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PwshPath {
  $pwshCommand = (Get-Command pwsh.exe -ErrorAction SilentlyContinue) ?? (Get-Command pwsh -ErrorAction SilentlyContinue)
  if ($null -eq $pwshCommand) {
    throw 'PowerShell 7+ executable was not found. Install PowerShell 7 or add pwsh.exe to PATH.'
  }

  $pwshCommand.Source
}

function Quote-WindowsArgument {
  param([Parameter(Mandatory)][string]$Value)

  if ($Value.IndexOf([char]0) -ge 0) {
    throw 'Arguments containing a NUL character are not supported for elevation relaunch.'
  }

  # Start-Process receives one native command-line string. Apply the Win32 argv
  # escaping rules to every value: quote it, double backslashes before a quote,
  # and double trailing backslashes before the closing quote. This preserves
  # roots such as D:\ and names containing spaces or ampersands.
  $builder = [System.Text.StringBuilder]::new()
  [void]$builder.Append('"')
  $backslashCount = 0
  foreach ($character in $Value.ToCharArray()) {
    if ($character -eq [char]92) {
      $backslashCount++
      continue
    }

    if ($character -eq [char]34) {
      [void]$builder.Append([string]::new([char]92, ($backslashCount * 2) + 1))
      [void]$builder.Append('"')
    } elseif ($backslashCount -gt 0) {
      [void]$builder.Append([string]::new([char]92, $backslashCount))
      [void]$builder.Append($character)
    } else {
      [void]$builder.Append($character)
    }
    $backslashCount = 0
  }

  if ($backslashCount -gt 0) {
    [void]$builder.Append([string]::new([char]92, $backslashCount * 2))
  }
  [void]$builder.Append('"')
  $builder.ToString()
}

function New-RelaunchArgumentString {
  param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][hashtable]$BoundParameters
  )

  $tokens = [System.Collections.Generic.List[string]]::new()
  foreach ($token in @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File')) {
    $tokens.Add($token)
  }
  $tokens.Add((Quote-WindowsArgument $ScriptPath))

  foreach ($entry in $BoundParameters.GetEnumerator()) {
    $name = $entry.Key
    $value = $entry.Value
    if ($value -is [System.Management.Automation.SwitchParameter]) {
      if ($value.IsPresent) { $tokens.Add("-$name") }
      continue
    }
    if ($value -is [bool]) {
      if ($value) { $tokens.Add("-$name") }
      continue
    }
    if ($null -eq $value) { continue }
    $tokens.Add("-$name")
    $tokens.Add((Quote-WindowsArgument ([string]$value)))
  }

  $tokens -join ' '
}

function Invoke-ElevatedSelf {
  $scriptPath = $PSCommandPath ?? $MyInvocation.MyCommand.Path
  if ([string]::IsNullOrWhiteSpace($scriptPath)) {
    throw 'Unable to determine script path for administrator relaunch.'
  }

  # $PSBoundParameters inside this helper belongs to the helper itself, rather
  # than to the script invocation. Build the child invocation explicitly so a
  # non-default Distro/VHDX/timeout survives the UAC relaunch.
  $relaunchParameters = [ordered]@{
    Distro = $Distro
    Vhdx = $Vhdx
    ShutdownMode = $ShutdownMode
    ShutdownTimeoutSeconds = $ShutdownTimeoutSeconds
  }
  if ($Force.IsPresent) { $relaunchParameters['Force'] = $Force }
  if ($NoLog.IsPresent) { $relaunchParameters['NoLog'] = $NoLog }
  if ($AllowVhdxMismatch.IsPresent) { $relaunchParameters['AllowVhdxMismatch'] = $AllowVhdxMismatch }
  if (-not [string]::IsNullOrWhiteSpace($LogDir)) {
    $relaunchParameters['LogDir'] = $LogDir
  }

  $argumentString = New-RelaunchArgumentString -ScriptPath $scriptPath -BoundParameters $relaunchParameters
  $process = Start-Process -FilePath (Get-PwshPath) -Verb RunAs -ArgumentList $argumentString -Wait -PassThru
  exit ($process.ExitCode ?? 1)
}

function Invoke-NativeCommand {
  param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string[]]$Arguments,
    [Parameter(Mandatory)][string]$Operation,
    [switch]$AllowNonZeroExit
  )

  $renderedArguments = ($Arguments | ForEach-Object {
      if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ }
    }) -join ' '
  Write-Log -Level CMD -Message "[$Operation] START: $FilePath $renderedArguments"

  $started = Get-Date
  $output = @(& $FilePath @Arguments 2>&1)
  $exitCode = $LASTEXITCODE
  $durationMs = [math]::Round(((Get-Date) - $started).TotalMilliseconds)

  foreach ($line in $output) {
    $text = $line.ToString().TrimEnd()
    if ($text) { Write-Log -Level DEBUG -Message "[$Operation] $text" }
  }
  Write-Log -Level CMD -Message "[$Operation] EXIT: $exitCode (${durationMs}ms)"

  $result = [pscustomobject]@{
    Operation = $Operation
    FilePath = $FilePath
    Arguments = $Arguments
    ExitCode = $exitCode
    DurationMs = $durationMs
    Output = @($output | ForEach-Object { $_.ToString() })
  }
  $script:CommandHistory.Add($result)

  if ($exitCode -ne 0 -and -not $AllowNonZeroExit) {
    throw "$Operation failed with exit code $exitCode. Review the detailed log for command output."
  }

  $result
}

function Get-WslDistroMetadata {
  param([Parameter(Mandatory)][string]$Name)

  $lxssRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
  if (-not (Test-Path -LiteralPath $lxssRoot)) { return $null }

  foreach ($key in Get-ChildItem -LiteralPath $lxssRoot -ErrorAction Stop) {
    $properties = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
    if ($properties.DistributionName -eq $Name) {
      return [pscustomobject]@{
        DistributionName = [string]$properties.DistributionName
        BasePath = [string]$properties.BasePath
        VhdxPath = Join-Path ([string]$properties.BasePath) 'ext4.vhdx'
      }
    }
  }

  $null
}

function Test-DistroVhdxMapping {
  param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$TargetVhdx
  )

  $metadata = Get-WslDistroMetadata -Name $Name
  if ($null -eq $metadata) {
    Write-Log -Level WARN -Message "No Lxss registry mapping found for distro '$Name'; VHDX mapping could not be independently verified."
    return
  }

  $expected = [System.IO.Path]::GetFullPath($metadata.VhdxPath)
  Write-Log -Message "Registry mapping: distro '$Name' -> $expected"
  if ($expected -ine $TargetVhdx) {
    $message = "Distro/VHDX mismatch. '$Name' maps to '$expected', but requested VHDX is '$TargetVhdx'."
    if ($AllowVhdxMismatch) {
      Write-Log -Level WARN -Message "$message Continuing because -AllowVhdxMismatch was supplied."
      return
    }
    throw "$message Supply -AllowVhdxMismatch only when the alternate VHDX is intentional."
  }
}

function Get-DriveSnapshot {
  param([Parameter(Mandatory)][string]$Path)

  $root = [System.IO.Path]::GetPathRoot($Path)
  $driveName = $root.TrimEnd('\').TrimEnd(':')
  $drive = Get-PSDrive -Name $driveName -ErrorAction Stop
  [pscustomobject]@{
    Root = $root
    FreeBytes = [Int64]$drive.Free
    UsedBytes = [Int64]$drive.Used
  }
}

function Get-SparseFlag {
  param([Parameter(Mandatory)][string]$Path)

  try {
    $result = Invoke-NativeCommand -FilePath 'fsutil.exe' -Arguments @('sparse', 'queryflag', $Path) -Operation 'Query sparse VHDX flag' -AllowNonZeroExit
    $text = $result.Output -join "`n"
    if ($result.ExitCode -ne 0) { return $null }
    if ($text -match '(?i)(not.*sparse|没有.*稀疏|未.*标记.*稀疏|未.*设置.*稀疏|不是.*稀疏)') { return $false }
    if ($text -match '(?i)(sparse|稀疏)') { return $true }
    return $null
  } catch {
    Write-Log -Level WARN -Message "Sparse flag query failed: $($_.Exception.Message)"
    return $null
  }
}

function Assert-DiskPartCompatiblePath {
  param([Parameter(Mandatory)][string]$Path)

  # DiskPart script input is emitted as ASCII for deterministic parsing. Check
  # before the shutdown phase so an unsupported path leaves running WSL alone.
  if ($Path -match '[^\x00-\x7F]') {
    throw 'The VHDX path contains non-ASCII characters. DiskPart input is restricted to ASCII paths for reliable script encoding.'
  }
}

function Get-VhdxSnapshot {
  param([Parameter(Mandatory)][string]$Path)

  $item = Get-Item -LiteralPath $Path -ErrorAction Stop
  $drive = Get-DriveSnapshot -Path $Path
  [pscustomobject]@{
    Timestamp = (Get-Date).ToString('o')
    Path = $item.FullName
    FileLengthBytes = [Int64]$item.Length
    LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    IsSparse = Get-SparseFlag -Path $item.FullName
    DriveRoot = $drive.Root
    DriveFreeBytes = $drive.FreeBytes
    DriveUsedBytes = $drive.UsedBytes
  }
}

function Write-VhdxSnapshot {
  param(
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)]$Snapshot
  )

  $sparse = if ($null -eq $Snapshot.IsSparse) { 'unknown' } else { [string]$Snapshot.IsSparse }
  Write-Log -Message "$Label VHDX length: $(Format-Size $Snapshot.FileLengthBytes)"
  Write-Log -Message "$Label drive $($Snapshot.DriveRoot) free: $(Format-Size $Snapshot.DriveFreeBytes); used: $(Format-Size $Snapshot.DriveUsedBytes); sparse: $sparse"
  Write-Log -Level DEBUG -Message "$Label VHDX last write (UTC): $($Snapshot.LastWriteTimeUtc)"
}

function Get-RunningWslDistros {
  $result = Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--list', '--running', '--quiet') -Operation 'List running WSL distros' -AllowNonZeroExit
  if ($result.ExitCode -ne 0) {
    throw 'Failed to query running WSL distributions.'
  }

  @($result.Output | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Write-WslInventory {
  $result = Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--list', '--verbose') -Operation 'WSL inventory' -AllowNonZeroExit
  if ($result.ExitCode -ne 0) {
    Write-Log -Level WARN -Message 'Could not collect detailed WSL inventory.'
  }
}

function Wait-ForWslStop {
  param(
    [Parameter(Mandatory)][ValidateSet('Global', 'Distro')][string]$Mode,
    [Parameter(Mandatory)][string]$TargetDistro,
    [Parameter(Mandatory)][int]$TimeoutSeconds
  )

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $running = Get-RunningWslDistros
    $stopped = if ($Mode -eq 'Global') {
      $running.Count -eq 0
    } else {
      $running -notcontains $TargetDistro
    }

    if ($stopped) {
      Write-Log -Message "WSL shutdown state confirmed for mode '$Mode'."
      return
    }

    Write-Log -Level DEBUG -Message "Waiting for WSL shutdown. Still running: $($running -join ', ')"
    Start-Sleep -Seconds 1
  } while ((Get-Date) -lt $deadline)

  throw "WSL did not reach the requested '$Mode' shutdown state within $TimeoutSeconds seconds."
}

function New-DiskPartScript {
  param([Parameter(Mandatory)][string[]]$Commands)

  $path = Join-Path $env:TEMP ('wsl-compact-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
  [System.IO.File]::WriteAllText($path, (($Commands + 'exit') -join [Environment]::NewLine), [System.Text.Encoding]::ASCII)
  $path
}

function Invoke-DiskPart {
  param(
    [Parameter(Mandatory)][string]$Operation,
    [Parameter(Mandatory)][string[]]$Commands,
    [switch]$RequireCompactSuccess
  )

  $diskPartScript = New-DiskPartScript -Commands $Commands
  try {
    Write-Log -Level DEBUG -Message "[$Operation] DiskPart script: $($Commands -join ' | ')"
    $result = Invoke-NativeCommand -FilePath 'diskpart.exe' -Arguments @('/s', $diskPartScript) -Operation $Operation -AllowNonZeroExit
    $text = $result.Output -join "`n"
    if ($result.ExitCode -ne 0 -or $text -match '(?i)(DiskPart has encountered an error|\berror\b|错误)') {
      throw "$Operation failed. DiskPart exit code: $($result.ExitCode)."
    }

    if ($RequireCompactSuccess -and $text -notmatch '(?i)(successfully compacted|成功压缩)') {
      Write-Log -Level WARN -Message "$Operation completed without an explicit compact-success marker; compare the before/after VHDX snapshots."
    }
    $result
  } finally {
    Remove-Item -LiteralPath $diskPartScript -Force -ErrorAction SilentlyContinue
  }
}

function Save-RunSummary {
  if (-not $script:SummaryPath) { return }

  $ended = Get-Date
  $summary = [ordered]@{
    startedAt = $script:RunStarted.ToString('o')
    endedAt = $ended.ToString('o')
    durationSeconds = [math]::Round(($ended - $script:RunStarted).TotalSeconds, 3)
    success = ($script:ExitCode -eq 0)
    exitCode = $script:ExitCode
    distro = $Distro
    vhdx = $Vhdx
    shutdownMode = $ShutdownMode
    whatIf = [bool]$WhatIfPreference
    before = $script:BeforeSnapshot
    after = $script:AfterSnapshot
    commands = @($script:CommandHistory)
    error = $script:ErrorText
  }

  try {
    $json = $summary | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
      $script:SummaryPath,
      $json + [Environment]::NewLine,
      [System.Text.UTF8Encoding]::new($false)
    )
    Write-Log -Message "Run summary written: $script:SummaryPath"
  } catch {
    Write-Host "$(Get-Date -Format o) [WARN] Could not write JSON summary: $($_.Exception.Message)" -ForegroundColor Yellow
  }
}

function Confirm-Compact {
  if ($Force -or $WhatIfPreference) { return $true }

  $answer = Read-Host "Proceed to $ShutdownMode shutdown and compact '$Vhdx'? Type YES to continue"
  if ($answer -eq 'YES') { return $true }

  Write-Log -Level WARN -Message 'Cancelled by user before shutdown and compaction.'
  $false
}

# Resolve supplied paths before an elevation relaunch so custom relative LogDir values
# continue to point to the same destination in the elevated process.
$Vhdx = Resolve-AbsolutePath $Vhdx
if ($LogDir) { $LogDir = Resolve-AbsolutePath $LogDir }

if (-not (Test-IsAdministrator) -and -not $WhatIfPreference) {
  Invoke-ElevatedSelf
}

try {
  Start-RunLogging
  Write-Log -Message '=== WSL2 VHDX compact run started ==='
  Write-Log -Message "User: $([Security.Principal.WindowsIdentity]::GetCurrent().Name); admin: $(Test-IsAdministrator); PowerShell: $($PSVersionTable.PSVersion); PID: $PID"
  Write-Log -Message "Parameters: Distro='$Distro'; VHDX='$Vhdx'; ShutdownMode='$ShutdownMode'; Force=$Force; WhatIf=$WhatIfPreference; Timeout=${ShutdownTimeoutSeconds}s; AllowVhdxMismatch=$AllowVhdxMismatch"

  if (-not (Test-Path -LiteralPath $Vhdx -PathType Leaf)) {
    throw "VHDX not found: $Vhdx"
  }
  Assert-DiskPartCompatiblePath -Path $Vhdx

  $allDistros = Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--list', '--quiet') -Operation 'List installed WSL distros'
  if (@($allDistros.Output | ForEach-Object { $_.Trim() }) -notcontains $Distro) {
    throw "WSL distro not found: $Distro"
  }

  Test-DistroVhdxMapping -Name $Distro -TargetVhdx $Vhdx
  Write-WslInventory
  $version = Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--version') -Operation 'WSL version' -AllowNonZeroExit
  if ($version.ExitCode -ne 0) { Write-Log -Level WARN -Message 'Could not collect WSL version information.' }

  $script:BeforeSnapshot = Get-VhdxSnapshot -Path $Vhdx
  Write-VhdxSnapshot -Label 'Before' -Snapshot $script:BeforeSnapshot

  $runningBefore = Get-RunningWslDistros
  Write-Log -Message "Running WSL distros before action: $(if ($runningBefore.Count) { $runningBefore -join ', ' } else { '<none>' })"

  $actionDescription = "shutdown WSL in $ShutdownMode mode and compact the VHDX"
  if ($WhatIfPreference) {
    Write-Log -Level WARN -Message 'WHATIF: preflight completed. WSL shutdown and DiskPart compaction were skipped.'
    $script:ExitCode = 0
  } elseif (-not (Confirm-Compact)) {
    $script:ExitCode = 0
  } elseif (-not $PSCmdlet.ShouldProcess($Vhdx, $actionDescription)) {
    Write-Log -Level WARN -Message 'PowerShell ShouldProcess declined the requested action.'
    $script:ExitCode = 0
  } else {
    if ($ShutdownMode -eq 'Global') {
      Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--shutdown') -Operation 'Global WSL shutdown' | Out-Null
    } else {
      Invoke-NativeCommand -FilePath 'wsl.exe' -Arguments @('--terminate', $Distro) -Operation "Terminate distro $Distro" | Out-Null
    }
    Wait-ForWslStop -Mode $ShutdownMode -TargetDistro $Distro -TimeoutSeconds $ShutdownTimeoutSeconds

    $selectCommand = "select vdisk file=`"$Vhdx`""
    Invoke-DiskPart -Operation 'DiskPart VHDX preflight' -Commands @($selectCommand, 'detail vdisk') | Out-Null
    Invoke-DiskPart -Operation 'DiskPart compact VHDX' -Commands @($selectCommand, 'compact vdisk') -RequireCompactSuccess | Out-Null

    $script:AfterSnapshot = Get-VhdxSnapshot -Path $Vhdx
    Write-VhdxSnapshot -Label 'After' -Snapshot $script:AfterSnapshot
    $savedBytes = $script:BeforeSnapshot.FileLengthBytes - $script:AfterSnapshot.FileLengthBytes
    $driveFreeDelta = $script:AfterSnapshot.DriveFreeBytes - $script:BeforeSnapshot.DriveFreeBytes
    Write-Log -Message "VHDX length delta: $(Format-Size ([math]::Abs($savedBytes))) $(if ($savedBytes -ge 0) { 'reclaimed' } else { 'increased' })"
    Write-Log -Message "Host drive free-space delta: $(Format-Size ([math]::Abs($driveFreeDelta))) $(if ($driveFreeDelta -ge 0) { 'gained' } else { 'reduced' })"
    if ($savedBytes -eq 0) {
      Write-Log -Level WARN -Message 'DiskPart completed but VHDX length was unchanged. This is possible when no blocks are currently reclaimable.'
    }

    Write-WslInventory
    $script:ExitCode = 0
  }
} catch {
  $script:ErrorText = $_ | Out-String
  Write-Log -Level ERROR -Message "Failure: $($_.Exception.Message)"
  Write-Log -Level ERROR -Message "Details: $($script:ErrorText.Trim())"
  $script:ExitCode = 1
} finally {
  Save-RunSummary
  if ($script:ExitCode -eq 0) {
    Write-Log -Message '=== WSL2 VHDX compact run finished successfully ==='
  } else {
    Write-Log -Level ERROR -Message '=== WSL2 VHDX compact run finished with errors ==='
  }
}

exit $script:ExitCode
