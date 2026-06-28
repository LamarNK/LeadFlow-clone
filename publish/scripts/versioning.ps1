function Convert-ToFourPartVersion {
    param([string]$Value)

    $parsed = $null
    if (-not [System.Version]::TryParse($Value, [ref]$parsed)) {
        return $null
    }

    $build = if ($parsed.Build -ge 0) { $parsed.Build } else { 0 }
    $revision = if ($parsed.Revision -ge 0) { $parsed.Revision } else { 0 }
    [System.Version]::new($parsed.Major, $parsed.Minor, $build, $revision)
}

function Get-ProjectVersion {
    param(
        [string]$RepoRoot,
        [string]$ProjectPath
    )

    $fullPath = Join-Path $RepoRoot $ProjectPath
    if (-not (Test-Path $fullPath)) {
        return $null
    }

    [xml]$projectXml = Get-Content -LiteralPath $fullPath
    $versionNode = $projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
    if ($versionNode) {
        return Convert-ToFourPartVersion $versionNode
    }

    return $null
}

function Read-VersionState {
    param([string]$StateRoot)

    $statePath = Join-Path $StateRoot "versions.json"
    if (-not (Test-Path $statePath)) {
        return @{}
    }

    $raw = Get-Content -LiteralPath $statePath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return @{}
    }

    $obj = $raw | ConvertFrom-Json
    $result = @{}
    foreach ($property in $obj.PSObject.Properties) {
        $result[$property.Name] = [string]$property.Value
    }
    return $result
}

function Read-VersionSeed {
    param([string]$PublishRoot)

    $seedPath = Join-Path $PublishRoot "version-seed.json"
    if (-not (Test-Path $seedPath)) {
        return @{}
    }

    $raw = Get-Content -LiteralPath $seedPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return @{}
    }

    $obj = $raw | ConvertFrom-Json
    $result = @{}
    foreach ($property in $obj.PSObject.Properties) {
        $result[$property.Name] = [string]$property.Value
    }
    return $result
}

function Write-VersionState {
    param(
        [string]$StateRoot,
        [hashtable]$State
    )

    $ordered = [ordered]@{}
    foreach ($name in ($State.Keys | Sort-Object)) {
        $ordered[$name] = $State[$name]
    }

    New-Item -Path $StateRoot -ItemType Directory -Force | Out-Null
    $statePath = Join-Path $StateRoot "versions.json"
    $ordered | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}

function Compare-VersionParts {
    param(
        [System.Version]$Left,
        [System.Version]$Right
    )

    foreach ($part in @("Major", "Minor", "Build", "Revision")) {
        if ($Left.$part -lt $Right.$part) { return -1 }
        if ($Left.$part -gt $Right.$part) { return 1 }
    }

    return 0
}

function Get-HighestVersion {
    param($Versions)

    $items = @($Versions) | Where-Object { $_ }
    if ($items.Count -eq 0) {
        return $null
    }

    return $items | Sort-Object Major, Minor, Build, Revision | Select-Object -Last 1
}

function Increment-Version {
    param(
        [System.Version]$Version,
        [ValidateSet("revision", "build", "minor", "major")]
        [string]$Bump
    )

    switch ($Bump) {
        "revision" { return [System.Version]::new($Version.Major, $Version.Minor, $Version.Build, ($Version.Revision + 1)) }
        "build" { return [System.Version]::new($Version.Major, $Version.Minor, ($Version.Build + 1), 1) }
        "minor" { return [System.Version]::new($Version.Major, ($Version.Minor + 1), 0, 1) }
        "major" { return [System.Version]::new(($Version.Major + 1), 0, 0, 1) }
    }
}

function Get-AutoIncrementedVersion {
    param(
        [System.Version]$Version,
        [int]$MaxRevision
    )

    if ($Version.Revision -ge $MaxRevision) {
        return @{
            Version = Increment-Version -Version $Version -Bump "build"
            Bump = "build (revision >= $MaxRevision)"
        }
    }

    return @{
        Version = Increment-Version -Version $Version -Bump "revision"
        Bump = "revision"
    }
}

function Get-NextVersion {
    param(
        [string]$Name,
        [hashtable]$Info,
        [hashtable]$State,
        [hashtable]$Seed,
        [string]$RepoRoot,
        [string]$OutRoot,
        [ValidateSet("auto", "revision", "build", "minor", "major")]
        [string]$VersionBump,
        [int]$MaxAutoRevision
    )

    $publishedCandidates = New-Object System.Collections.Generic.List[System.Version]
    $baselineCandidates = New-Object System.Collections.Generic.List[System.Version]

    if ($State.ContainsKey($Name)) {
        $stateVersion = Convert-ToFourPartVersion $State[$Name]
        if ($stateVersion) { $publishedCandidates.Add($stateVersion) }
    }

    if ($Seed.ContainsKey($Name)) {
        $seedVersion = Convert-ToFourPartVersion $Seed[$Name]
        if ($seedVersion) { $baselineCandidates.Add($seedVersion) }
    }

    $targetOut = Join-Path $OutRoot $Name
    if (Test-Path $targetOut) {
        Get-ChildItem -LiteralPath $targetOut -Directory | ForEach-Object {
            $dirVersion = Convert-ToFourPartVersion $_.Name
            if ($dirVersion) { $publishedCandidates.Add($dirVersion) }
        }
    }

    $projectVersion = Get-ProjectVersion -RepoRoot $RepoRoot -ProjectPath $Info.Project
    if ($projectVersion) {
        $baselineCandidates.Add($projectVersion)
    }

    $latestPublished = Get-HighestVersion $publishedCandidates
    $latestBaseline = Get-HighestVersion $baselineCandidates
    $fallbackVersion = [System.Version]::new(1, 0, 0, 0)
    $latestKnown = Get-HighestVersion (@($publishedCandidates) + @($baselineCandidates))

    if (-not $latestKnown) {
        $latestKnown = $fallbackVersion
    }

    if ($VersionBump -ne "auto") {
        return @{
            Version = Increment-Version -Version $latestKnown -Bump $VersionBump
            Base = $latestKnown
            Bump = $VersionBump
        }
    }

    $baseVersion = $null
    if ($latestPublished -and $latestBaseline) {
        if ((Compare-VersionParts -Left $latestBaseline -Right $latestPublished) -gt 0) {
            $baseVersion = $latestBaseline
        }
        else {
            $baseVersion = $latestPublished
        }
    }
    elseif ($latestBaseline) {
        $baseVersion = $latestBaseline
    }
    elseif ($latestPublished) {
        $baseVersion = $latestPublished
    }
    else {
        $baseVersion = $fallbackVersion
    }

    $auto = Get-AutoIncrementedVersion -Version $baseVersion -MaxRevision $MaxAutoRevision
    return @{
        Version = $auto.Version
        Base = $baseVersion
        Bump = $auto.Bump
    }
}

function Convert-ToMsiProductVersion {
    param([string]$VersionText)

    $parsed = Convert-ToFourPartVersion $VersionText
    if (-not $parsed) {
        throw "Invalid ProductVersion '$VersionText'."
    }

    if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 255 -or $parsed.Revision -gt 255) {
        throw "MSI version parts must be in range 0..255 before encoding. Got $VersionText."
    }

    $msiBuild = ($parsed.Build * 256) + $parsed.Revision
    "{0}.{1}.{2}" -f $parsed.Major, $parsed.Minor, $msiBuild
}

function Write-BuildManifest {
    param(
        [string]$TargetOut,
        [string]$Name,
        [string]$VersionText,
        [string]$Configuration,
        [string]$Runtime,
        [string]$SourceProject,
        [hashtable]$Extra = @{}
    )

    $manifest = [ordered]@{
        target = $Name
        version = $VersionText
        configuration = $Configuration
        runtime = $Runtime
        builtAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        sourceProject = $SourceProject
    }

    foreach ($key in ($Extra.Keys | Sort-Object)) {
        $manifest[$key] = $Extra[$key]
    }

    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TargetOut "orbita-build.json") -Encoding UTF8
}