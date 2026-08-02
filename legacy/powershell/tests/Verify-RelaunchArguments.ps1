#requires -Version 7.0
<#
  Unit-level verification for UAC relaunch argument rendering. It evaluates only
  the two pure helper functions and never invokes WSL, DiskPart, or UAC.
#>
[CmdletBinding()]
param(
  [string]$ScriptPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'wsl.ps1')
)

$ErrorActionPreference = 'Stop'

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
  throw ('PowerShell parse failed: ' + (($errors | ForEach-Object Message) -join '; '))
}

$requiredNames = @('Quote-WindowsArgument', 'New-RelaunchArgumentString')
$functions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in $requiredNames
  }, $true))

foreach ($name in $requiredNames) {
  $definition = @($functions | Where-Object Name -eq $name | Select-Object -First 1)
  if ($definition.Count -ne 1) {
    throw "Required helper function '$name' was not found."
  }
  . ([scriptblock]::Create($definition[0].Extent.Text))
}

$switchOn = [System.Management.Automation.SwitchParameter]::new($true)
$rendered = New-RelaunchArgumentString -ScriptPath 'C:\Tools With Space\wsl.ps1' -BoundParameters ([ordered]@{
  Distro = 'Ubuntu Custom'
  Vhdx = 'E:\VHDX With Space\ext4.vhdx'
  ShutdownMode = 'Distro'
  ShutdownTimeoutSeconds = 90
  LogDir = 'D:\'
  Force = $switchOn
  NoLog = $false
})

$expectedFragments = @(
  '-File "C:\Tools With Space\wsl.ps1"',
  '-Distro "Ubuntu Custom"',
  '-Vhdx "E:\VHDX With Space\ext4.vhdx"',
  '-ShutdownMode "Distro"',
  '-ShutdownTimeoutSeconds "90"',
  '-LogDir "D:\\"',
  '-Force'
)

foreach ($fragment in $expectedFragments) {
  if (-not $rendered.Contains($fragment)) {
    throw "Missing relaunch argument fragment: $fragment`nActual: $rendered"
  }
}
if ($rendered -match '(?i)-NoLog(?:\s|$)' -or $rendered -match '(?i)-Force\s+True') {
  throw "Switch argument rendering is invalid: $rendered"
}

$tempRoot = [System.IO.Path]::GetTempPath()
$suffix = [guid]::NewGuid().ToString('N')
$payloadPath = Join-Path $tempRoot "wsl-compact-relaunch-payload-$suffix.ps1"
$outputPath = Join-Path $tempRoot "wsl-compact-relaunch-output-$suffix.json"
$payload = @'
param(
  [string]$Distro,
  [string]$Vhdx,
  [string]$ShutdownMode,
  [int]$ShutdownTimeoutSeconds,
  [string]$LogDir,
  [switch]$Force,
  [switch]$NoLog
)

[ordered]@{
  Distro = $Distro
  Vhdx = $Vhdx
  ShutdownMode = $ShutdownMode
  ShutdownTimeoutSeconds = $ShutdownTimeoutSeconds
  LogDir = $LogDir
  Force = $Force.IsPresent
  NoLog = $NoLog.IsPresent
} | ConvertTo-Json -Compress
'@

try {
  [System.IO.File]::WriteAllText($payloadPath, $payload, [System.Text.UTF8Encoding]::new($false))
  $childArguments = New-RelaunchArgumentString -ScriptPath $payloadPath -BoundParameters ([ordered]@{
    Distro = 'Ubuntu Custom'
    Vhdx = 'E:\VHDX With Space\ext4.vhdx'
    ShutdownMode = 'Distro'
    ShutdownTimeoutSeconds = 90
    LogDir = 'D:\'
    Force = $switchOn
    NoLog = $false
  })
  $child = Start-Process -FilePath (Get-Command pwsh.exe -ErrorAction Stop).Source -ArgumentList $childArguments -Wait -PassThru -RedirectStandardOutput $outputPath
  if ($child.ExitCode -ne 0) {
    throw "Child PowerShell process failed with exit code $($child.ExitCode)."
  }

  $received = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
  if ($received.Distro -ne 'Ubuntu Custom' -or
      $received.Vhdx -ne 'E:\VHDX With Space\ext4.vhdx' -or
      $received.ShutdownMode -ne 'Distro' -or
      $received.ShutdownTimeoutSeconds -ne 90 -or
      $received.LogDir -ne 'D:\' -or
      -not $received.Force -or $received.NoLog) {
    throw "Child process received incorrect relaunch arguments: $($received | ConvertTo-Json -Compress)"
  }
} finally {
  Remove-Item -LiteralPath $payloadPath, $outputPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'PASS: UAC relaunch arguments preserve values and child-process parsing.' -ForegroundColor Green
