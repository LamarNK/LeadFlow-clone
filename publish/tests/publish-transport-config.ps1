$ErrorActionPreference = "Stop"

$publishScript = Join-Path $PSScriptRoot "..\publish.bat"
$content = Get-Content -LiteralPath $publishScript -Raw

$requiredDefaults = @(
    'if not defined LEADFLOW_SSH_CONNECT_TIMEOUT set "LEADFLOW_SSH_CONNECT_TIMEOUT=15"',
    'if not defined LEADFLOW_SSH_ALIVE_INTERVAL set "LEADFLOW_SSH_ALIVE_INTERVAL=15"',
    'if not defined LEADFLOW_SSH_ALIVE_COUNT_MAX set "LEADFLOW_SSH_ALIVE_COUNT_MAX=4"'
)

foreach ($expected in $requiredDefaults) {
    if (-not $content.Contains($expected)) {
        throw "Missing SSH timeout default: $expected"
    }
}

foreach ($variable in @("SSH_ARGS", "SCP_ARGS")) {
    $lines = @($content -split "`r?`n" | Where-Object { $_ -like "set `"$variable=*" })
    if ($lines.Count -ne 1) {
        throw "Expected one $variable definition, found $($lines.Count)."
    }

    $line = $lines[0]

    foreach ($option in @("ConnectionAttempts=1", "ConnectTimeout=!LEADFLOW_SSH_CONNECT_TIMEOUT!", "ServerAliveInterval=!LEADFLOW_SSH_ALIVE_INTERVAL!", "ServerAliveCountMax=!LEADFLOW_SSH_ALIVE_COUNT_MAX!")) {
        if (-not $line.Contains($option)) {
            throw "$variable is missing $option."
        }
    }
}

Write-Host "Publish transport timeout configuration is valid."
