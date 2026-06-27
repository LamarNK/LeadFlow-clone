param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("plan", "pack", "save")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$Target,

    [Parameter(Mandatory = $true)]
    [string]$StageDir,

    [string]$StateDir = "",
    [string]$ArchivePath = "",
    [string]$PlanPath = "",
    [string]$DeltaListPath = "",
    [int]$DeltaThresholdPercent = 30
)

$ErrorActionPreference = "Stop"

function Get-DefaultStateDir {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $MyInvocation.PSCommandPath) {
        $scriptDir = Split-Path -Parent $MyInvocation.PSCommandPath
    }

    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        throw "Cannot resolve publish state directory."
    }

    return (Join-Path (Split-Path -Parent $scriptDir) "state")
}

if ([string]::IsNullOrWhiteSpace($StateDir)) {
    $StateDir = Get-DefaultStateDir
}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    $base = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $base.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $base += [System.IO.Path]::DirectorySeparatorChar
    }

    $full = [System.IO.Path]::GetFullPath($FullPath)
    return $full.Substring($base.Length).Replace("\", "/")
}

function Get-StageManifest {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        throw "Stage directory not found: $Root"
    }

    $files = @{}
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = Get-RelativePath -BasePath $Root -FullPath $_.FullName
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $files[$relative] = $hash
    }

    $lines = $files.Keys | Sort-Object | ForEach-Object { "$($_):$($files[$_])" }
    $fingerprint = if ($lines.Count -eq 0) {
        "empty"
    }
    else {
        $joined = [string]::Join("`n", $lines)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($joined)
        $stream = [System.IO.MemoryStream]::new($bytes)
        try {
            (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }

    return [pscustomobject]@{
        Fingerprint = $fingerprint
        Files = $files
    }
}

function Read-StateFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json
}

function Write-Plan {
    param(
        [string]$Path,
        [string]$Mode,
        [string[]]$ChangedFiles = @(),
        [string[]]$DeletedFiles = @()
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "PlanPath is required."
    }

    $plan = [ordered]@{
        mode = $Mode
        changed = $ChangedFiles
        deleted = $DeletedFiles
    }

    $plan | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding UTF8
    Set-Content -LiteralPath ($Path + ".mode") -Value $Mode.ToLowerInvariant() -Encoding Ascii -NoNewline
}

function Invoke-Plan {
    if ([string]::IsNullOrWhiteSpace($PlanPath)) {
        throw "PlanPath is required for plan action."
    }

    $manifest = Get-StageManifest -Root $StageDir
    $stateFile = Join-Path $StateDir "$Target.deploy.json"
    $previous = Read-StateFile -Path $stateFile

    if ($env:LEADFLOW_FORCE_PUBLISH -eq "1") {
        Write-Plan -Path $PlanPath -Mode "FULL"
        Write-Host "Force publish enabled; uploading full context." -ForegroundColor Yellow
        return
    }

    if ($null -eq $previous -or $null -eq $previous.files) {
        Write-Plan -Path $PlanPath -Mode "FULL"
        Write-Host "No deploy state for $Target; full upload required." -ForegroundColor Yellow
        return
    }

    if ($previous.fingerprint -eq $manifest.Fingerprint) {
        Write-Plan -Path $PlanPath -Mode "SKIP"
        Write-Host "$Target is unchanged since last deploy; skipping upload." -ForegroundColor Green
        return
    }

    $previousFiles = @{}
    foreach ($entry in $previous.files.PSObject.Properties) {
        $previousFiles[$entry.Name] = [string]$entry.Value
    }

    $changed = New-Object "System.Collections.Generic.List[string]"
    foreach ($entry in $manifest.Files.GetEnumerator()) {
        $path = [string]$entry.Key
        $hash = [string]$entry.Value
        if (-not $previousFiles.ContainsKey($path) -or $previousFiles[$path] -ne $hash) {
            [void]$changed.Add($path)
        }
    }

    $deleted = New-Object "System.Collections.Generic.List[string]"
    foreach ($path in $previousFiles.Keys) {
        if (-not $manifest.Files.ContainsKey($path)) {
            [void]$deleted.Add([string]$path)
        }
    }

    $total = [math]::Max($manifest.Files.Count, 1)
    $changeCount = $changed.Count + $deleted.Count
    $changePercent = [math]::Round(($changeCount / $total) * 100, 2)

    if ($deleted.Count -gt 0 -or $changePercent -ge $DeltaThresholdPercent) {
        Write-Plan -Path $PlanPath -Mode "FULL" -ChangedFiles $changed.ToArray() -DeletedFiles $deleted.ToArray()
        Write-Host "$Target changed ($changeCount files, $changePercent%); full upload." -ForegroundColor Yellow
        return
    }

    Write-Plan -Path $PlanPath -Mode "DELTA" -ChangedFiles $changed.ToArray() -DeletedFiles $deleted.ToArray()
    Write-Host "$Target changed ($changeCount files, $changePercent%); delta upload." -ForegroundColor Cyan
}

function Invoke-Pack {
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        throw "ArchivePath is required for pack action."
    }
    if ([string]::IsNullOrWhiteSpace($PlanPath)) {
        throw "PlanPath is required for pack action."
    }
    if (-not (Test-Path -LiteralPath $PlanPath)) {
        throw "Plan file not found: $PlanPath"
    }

    $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $mode = [string]$plan.mode

    if ($mode -eq "SKIP") {
        return
    }

    $archiveDir = Split-Path -Parent $ArchivePath
    if (-not [string]::IsNullOrWhiteSpace($archiveDir)) {
        New-Item -Path $archiveDir -ItemType Directory -Force | Out-Null
    }

    if (Test-Path -LiteralPath $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    if ($mode -eq "FULL") {
        & tar -czf $ArchivePath -C $StageDir .
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed while creating full archive."
        }
        return
    }

    if ($mode -ne "DELTA") {
        throw "Unknown deploy mode: $mode"
    }

    $changed = @()
    if ($null -ne $plan.changed) {
        $changed = @($plan.changed | ForEach-Object { [string]$_ })
    }

    if ($changed.Count -eq 0) {
        throw "Delta plan has no changed files."
    }

    $deleted = @()
    if ($null -ne $plan.deleted) {
        $deleted = @($plan.deleted | ForEach-Object { [string]$_ })
    }

    $packRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("leadflow-delta-" + [guid]::NewGuid().ToString("N"))
    New-Item -Path $packRoot -ItemType Directory -Force | Out-Null

    try {
        foreach ($relative in $changed) {
            $source = Join-Path $StageDir ($relative.Replace("/", "\"))
            if (-not (Test-Path -LiteralPath $source)) {
                throw "Changed file missing from stage: $relative"
            }

            $destination = Join-Path $packRoot ($relative.Replace("/", "\"))
            $destinationDir = Split-Path -Parent $destination
            if (-not [string]::IsNullOrWhiteSpace($destinationDir)) {
                New-Item -Path $destinationDir -ItemType Directory -Force | Out-Null
            }

            Copy-Item -LiteralPath $source -Destination $destination -Force
        }

        if ($deleted.Count -gt 0) {
            $deleted | Set-Content -LiteralPath (Join-Path $packRoot "deleted.txt") -Encoding UTF8
        }

        & tar -czf $ArchivePath -C $packRoot .
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed while creating delta archive."
        }
    }
    finally {
        if (Test-Path -LiteralPath $packRoot) {
            Remove-Item -LiteralPath $packRoot -Recurse -Force
        }
    }
}

function Invoke-Save {
    New-Item -Path $StateDir -ItemType Directory -Force | Out-Null

    $manifest = Get-StageManifest -Root $StageDir
    $state = [ordered]@{
        target = $Target
        fingerprint = $manifest.Fingerprint
        deployedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        files = $manifest.Files
    }

    $stateFile = Join-Path $StateDir "$Target.deploy.json"
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stateFile -Encoding UTF8
}

$StageDir = (Resolve-Path -LiteralPath $StageDir).Path

switch ($Action) {
    "plan" { Invoke-Plan }
    "pack" { Invoke-Pack }
    "save" { Invoke-Save }
    default { throw "Unknown action: $Action" }
}