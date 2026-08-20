[CmdletBinding()]
param(
    [string]$CoveragePath = "artifacts/coverage/coverage.cobertura.xml",
    [double]$MinimumLineRate = 0.80
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CoveragePath -PathType Leaf)) {
    throw "Coverage report not found: $CoveragePath"
}

[xml]$report = Get-Content -LiteralPath $CoveragePath -Raw
$packages = @($report.coverage.packages.package)
$required = @("Vela.Core", "Vela.Windows", "Vela.Application", "Vela.Tui")
$seen = @{}

foreach ($name in $required) {
    $package = $packages | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if ($null -eq $package) {
        throw "Coverage package not found: $name"
    }

    $rate = [double]$package.'line-rate'
    $seen[$name] = $rate
    $percent = $rate * 100
    Write-Host ("{0}: {1:N2}% line coverage" -f $name, $percent)
    if ($rate -lt $MinimumLineRate) {
        throw ("{0} line coverage {1:P2} is below the required {2:P2}." -f $name, $rate, $MinimumLineRate)
    }
}

Write-Host ("Coverage verification passed for {0} assemblies." -f $required.Count)
