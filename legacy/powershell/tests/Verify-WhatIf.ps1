#requires -Version 7.0
<#
  Read-only smoke test. It parses the main script and runs its WhatIf path,
  which must not shut down WSL or invoke DiskPart.
#>
[CmdletBinding()]
param(
  [string]$ScriptPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'wsl.ps1'),
  [string]$Vhdx = 'D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx'
)

$ErrorActionPreference = 'Stop'

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -gt 0) {
  $messages = @($errors | ForEach-Object { $_.Message })
  throw ('PowerShell parse failed: ' + ($messages -join '; '))
}

$before = Get-Item -LiteralPath $Vhdx -ErrorAction Stop
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source

# The main script uses `exit` to communicate its process status. Launch it in
# a child pwsh process so this verifier can always perform the after-check.
& $pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File $ScriptPath -Vhdx $before.FullName -WhatIf -NoLog
$whatIfExitCode = $LASTEXITCODE
if ($whatIfExitCode -ne 0) {
  throw "WhatIf smoke test failed with exit code $whatIfExitCode."
}
$after = Get-Item -LiteralPath $before.FullName -ErrorAction Stop
if ($after.Length -ne $before.Length) {
  throw 'WhatIf changed the VHDX size, which violates the read-only contract.'
}

Write-Host 'PASS: script parsed and WhatIf completed without changing the VHDX.' -ForegroundColor Green
