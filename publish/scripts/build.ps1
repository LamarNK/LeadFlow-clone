param(
    [ValidateSet("all", "leadflow", "orbita-worker")]
    [string]$Target = "leadflow",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [ValidateSet("auto", "revision", "build", "minor", "major")]
    [string]$VersionBump = "auto",
    [int]$MaxAutoRevision = 99,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "versioning.ps1")

$publishRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $publishRoot "..")
$outRoot = Join-Path $publishRoot "out"
$stateRoot = Join-Path $publishRoot "state"
$workRoot = Join-Path $publishRoot "tmp\build"

New-Item -Path $outRoot -ItemType Directory -Force | Out-Null
New-Item -Path $stateRoot -ItemType Directory -Force | Out-Null
New-Item -Path $workRoot -ItemType Directory -Force | Out-Null

$targets = [ordered]@{
    leadflow = @{
        Project = "LeadFlow\LeadFlow.csproj"
        OutputKind = "leadflow-zip"
        Runtime = "win-x64"
    }
    "orbita-worker" = @{
        Project = "Orbita.Worker\Orbita.Worker.csproj"
        OutputKind = "orbita-worker-msi"
        Runtime = "win-x64"
    }
}

function Invoke-DotnetPublish {
    param(
        [string]$ProjectPath,
        [string]$OutputPath,
        [string]$RuntimeName,
        [string]$Config,
        [string]$VersionText,
        [bool]$SelfContained = $false
    )

    if ($Clean -and (Test-Path $OutputPath)) {
        Remove-Item -LiteralPath $OutputPath -Recurse -Force
    }

    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null

    $args = @(
        "publish",
        $ProjectPath,
        "-c", $Config,
        "-r", $RuntimeName,
        "--self-contained", ($(if ($SelfContained) { "true" } else { "false" })),
        "-p:Version=$VersionText",
        "-p:FileVersion=$VersionText",
        "-p:InformationalVersion=$VersionText",
        "-p:IncludeSourceRevisionInInformationalVersion=false",
        "-o", $OutputPath
    )

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: $ProjectPath"
    }

    Get-ChildItem -LiteralPath $OutputPath -Recurse -Filter "*.pdb" -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function Get-UnicodeName {
    param([int[]]$CodePoints)
    return -join ($CodePoints | ForEach-Object { [char]$_ })
}

function Build-LeadFlowZip {
    param(
        [string]$VersionText,
        [string]$TargetOut,
        [hashtable]$Info
    )

    $targetRuntime = $Info.Runtime
    Write-Host "== Building LeadFlow $VersionText ($Configuration / $targetRuntime) ==" -ForegroundColor Cyan

    $docsFolderName = Get-UnicodeName @(0x0414, 0x043E, 0x043A, 0x0443, 0x043C, 0x0435, 0x043D, 0x0442, 0x0430, 0x0446, 0x0438, 0x044F)
    $startFileName = Get-UnicodeName @(0x041A, 0x0430, 0x043A, 0x0020, 0x0437, 0x0430, 0x043F, 0x0443, 0x0441, 0x0442, 0x0438, 0x0442, 0x044C) + ".txt"
    $packageName = "LeadFlow-Windows-x64-$VersionText"
    $layoutRoot = Join-Path $TargetOut $packageName
    $zipPath = Join-Path $TargetOut "$packageName.zip"

    $projectPath = Join-Path $repoRoot $Info.Project
    if (-not (Test-Path $projectPath)) {
        throw "LeadFlow project not found: $projectPath"
    }

    Remove-Item -LiteralPath $layoutRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

    $appOut = Join-Path $layoutRoot "LeadFlow"
    $docsOut = Join-Path $layoutRoot $docsFolderName
    New-Item -Path $appOut -ItemType Directory -Force | Out-Null
    New-Item -Path $docsOut -ItemType Directory -Force | Out-Null

    Invoke-DotnetPublish -ProjectPath $projectPath -OutputPath $appOut -RuntimeName $targetRuntime -Config $Configuration -VersionText $VersionText

    $readme = Join-Path $repoRoot "docs\README.md"
    $guide = Join-Path $repoRoot "docs\USER_GUIDE_RU.md"
    if (-not (Test-Path $readme)) { throw "Documentation not found: $readme" }
    if (-not (Test-Path $guide)) { throw "Documentation not found: $guide" }

    Copy-Item -LiteralPath $readme -Destination (Join-Path $docsOut "README.md") -Force
    Copy-Item -LiteralPath $guide -Destination (Join-Path $docsOut "USER_GUIDE_RU.md") -Force
    Copy-Item -LiteralPath (Join-Path $publishRoot "templates\how-to-start.txt") -Destination (Join-Path $layoutRoot $startFileName) -Force

    $exePath = Join-Path $appOut "LeadFlow.exe"
    if (-not (Test-Path $exePath)) {
        throw "LeadFlow.exe was not produced in $appOut"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $layoutRoot,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "Archive: $zipPath ($zipMb MB)" -ForegroundColor Green
}

function Stop-OrbitaWorkerForPackaging {
    $procs = Get-Process -Name "Orbita.Worker" -ErrorAction SilentlyContinue
    if (-not $procs) {
        return
    }

    Write-Host "Stopping running Orbita.Worker before MSI build..." -ForegroundColor Yellow
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Reset-WindowsInstallerService {
    $svc = Get-Service -Name msiserver -ErrorAction SilentlyContinue
    if (-not $svc) {
        throw "Windows Installer service (msiserver) is not available on this machine."
    }

    if ($svc.Status -eq 'Running') {
        return
    }

    try {
        Start-Service -Name msiserver
    } catch {
        throw "Cannot start Windows Installer service (msiserver). WiX MSI build requires it. Run PowerShell as Administrator and execute: Start-Service msiserver"
    }

    $svc.Refresh()
    if ($svc.Status -ne 'Running') {
        throw "Windows Installer service (msiserver) is not running (status: $($svc.Status)). Start it manually: Start-Service msiserver"
    }
}

function Get-BuiltMsiCandidate {
    param(
        [string[]]$SearchRoots,
        [string]$Config
    )

    $candidates = @()
    foreach ($root in $SearchRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        $candidates += Get-ChildItem -LiteralPath $root -Recurse -Filter "*.msi" -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -gt 1MB }
    }

    return $candidates |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Invoke-WixMsiBuild {
    param(
        [string]$MsiProject,
        [string]$PublishDir,
        [string]$MsiVersion,
        [string]$Config,
        [string]$IntermediateOutputPath,
        [string]$OutputPath,
        [int]$MaxAttempts = 3
    )

    $publishDirArg = if ($PublishDir.EndsWith('\')) { $PublishDir } else { "$PublishDir\" }
    $intermediateArg = if ($IntermediateOutputPath.EndsWith('\')) { $IntermediateOutputPath } else { "$IntermediateOutputPath\" }
    $outputArg = if ($OutputPath.EndsWith('\')) { $OutputPath } else { "$OutputPath\" }

    $searchRoots = @(
        (Join-Path $outputArg $Config),
        (Join-Path $intermediateArg $Config)
    )

    $builtMsi = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Host "MSI build retry $attempt/$MaxAttempts..." -ForegroundColor Yellow
            Reset-WindowsInstallerService
            Start-Sleep -Seconds 3
        }

        & dotnet build $MsiProject -c $Config `
            -p:PublishDir=$publishDirArg `
            -p:ProductVersion=$MsiVersion `
            -p:BaseIntermediateOutputPath=$intermediateArg `
            -p:BaseOutputPath=$outputArg `
            -p:OutputPath=$outputArg

        if ($LASTEXITCODE -eq 0) {
            $builtMsi = Get-BuiltMsiCandidate -SearchRoots $searchRoots -Config $Config
            if ($builtMsi) {
                return $builtMsi
            }
        } else {
            $builtMsi = Get-BuiltMsiCandidate -SearchRoots $searchRoots -Config $Config
            if ($builtMsi) {
                Write-Host "WiX reported failure but MSI artifact exists; continuing with $($builtMsi.FullName)" -ForegroundColor Yellow
                return $builtMsi
            }
        }
    }

    throw @"
dotnet build failed: $MsiProject
WiX native MSI error 1627 usually means Windows Installer could not finish packaging (often transient).
Try:
  1) Close Orbita.Worker and retry
  2) Run: Restart-Service msiserver
  3) Re-run with -Clean
  4) Free disk/RAM; avoid building under heavy load
"@
}

function Build-OrbitaWorkerMsi {
    param(
        [string]$VersionText,
        [string]$TargetOut,
        [hashtable]$Info
    )

    $targetRuntime = $Info.Runtime
    $workRootPublish = Join-Path $publishRoot "tmp\orbita-worker-publish"
    $msiProject = Join-Path $repoRoot "installer\Orbita.Worker.Msi\Orbita.Worker.Msi.wixproj"
    $localWixRoot = Join-Path $env:TEMP "orbita-worker-wix-msi"
    $localWixObj = Join-Path $localWixRoot "obj"
    $localWixBin = Join-Path $localWixRoot "bin"
    $projectPath = Join-Path $repoRoot $Info.Project
    $msiName = "Orbita.Worker.Setup-$VersionText.msi"
    $msiPath = Join-Path $TargetOut $msiName

    Write-Host "== Building Orbita Worker $VersionText ($Configuration / $targetRuntime) ==" -ForegroundColor Cyan

    if (-not (Test-Path $projectPath)) {
        throw "Orbita.Worker project not found: $projectPath"
    }
    if (-not (Test-Path $msiProject)) {
        throw "WiX project not found: $msiProject"
    }

    if ($Clean) {
        Remove-Item -LiteralPath $workRootPublish -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $localWixRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Invoke-DotnetPublish -ProjectPath $projectPath -OutputPath $workRootPublish -RuntimeName $targetRuntime -Config $Configuration -VersionText $VersionText -SelfContained $true

    $exePath = Join-Path $workRootPublish "Orbita.Worker.exe"
    if (-not (Test-Path $exePath)) {
        throw "Orbita.Worker.exe was not produced in $workRootPublish"
    }

    Stop-OrbitaWorkerForPackaging
    Reset-WindowsInstallerService

    # WiX native MSI backend is sensitive to network paths; stage publish on local disk.
    $localPublish = Join-Path $env:TEMP "orbita-worker-publish-msi"
    if (Test-Path $localPublish) {
        Remove-Item -LiteralPath $localPublish -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Staging publish output on local disk: $localPublish" -ForegroundColor DarkGray
    Copy-Item -LiteralPath $workRootPublish -Destination $localPublish -Recurse -Force

    New-Item -Path $localWixObj -ItemType Directory -Force | Out-Null
    New-Item -Path $localWixBin -ItemType Directory -Force | Out-Null

    $msiVersion = Convert-ToMsiProductVersion $VersionText
    Write-Host "== Building MSI (product version $msiVersion) ==" -ForegroundColor Cyan
    Write-Host "WiX intermediate/output on local disk: $localWixRoot" -ForegroundColor DarkGray
    $builtMsi = Invoke-WixMsiBuild `
        -MsiProject $msiProject `
        -PublishDir $localPublish `
        -MsiVersion $msiVersion `
        -Config $Configuration `
        -IntermediateOutputPath $localWixObj `
        -OutputPath $localWixBin

    if (-not $builtMsi) {
        throw "MSI was not produced by WiX build."
    }

    Copy-Item -LiteralPath $builtMsi.FullName -Destination $msiPath -Force
    $msiMb = [math]::Round((Get-Item $msiPath).Length / 1MB, 2)
    Write-Host "MSI: $msiPath ($msiMb MB)" -ForegroundColor Green

    Write-BuildManifest -TargetOut $TargetOut -Name "orbita-worker" -VersionText $VersionText -Configuration $Configuration -Runtime $targetRuntime -SourceProject $Info.Project -Extra @{
        msiProductVersion = $msiVersion
        packageFile = $msiName
    }
}

function Publish-Target {
    param(
        [string]$Name,
        [hashtable]$Info,
        [hashtable]$State,
        [hashtable]$Seed
    )

    $versionInfo = Get-NextVersion -Name $Name -Info $Info -State $State -Seed $Seed -RepoRoot $repoRoot -OutRoot $outRoot -VersionBump $VersionBump -MaxAutoRevision $MaxAutoRevision
    $versionText = $versionInfo.Version.ToString()
    Write-Host "Version: $versionText (from $($versionInfo.Base), bump $($versionInfo.Bump))"

    $targetOut = Join-Path (Join-Path $outRoot $Name) $versionText
    Remove-Item -LiteralPath $targetOut -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -Path $targetOut -ItemType Directory -Force | Out-Null

    switch ($Info.OutputKind) {
        "leadflow-zip" {
            Build-LeadFlowZip -VersionText $versionText -TargetOut $targetOut -Info $Info
            Write-BuildManifest -TargetOut $targetOut -Name $Name -VersionText $versionText -Configuration $Configuration -Runtime $Info.Runtime -SourceProject $Info.Project -Extra @{
                packageFile = "LeadFlow-Windows-x64-$versionText.zip"
            }
        }
        "orbita-worker-msi" {
            Build-OrbitaWorkerMsi -VersionText $versionText -TargetOut $targetOut -Info $Info
        }
        default {
            throw "Unknown output kind: $($Info.OutputKind)"
        }
    }

    $State[$Name] = $versionText
    Write-Host "Output: $targetOut"
}

$seed = Read-VersionSeed -PublishRoot $publishRoot
$state = Read-VersionState -StateRoot $stateRoot
$selectedTargets = if ($Target -eq "all") { @("leadflow", "orbita-worker") } else { @($Target) }

foreach ($name in $selectedTargets) {
    Write-Host ""
    Publish-Target -Name $name -Info $targets[$name] -State $state -Seed $seed
}

Write-VersionState -StateRoot $stateRoot -State $state

Write-Host ""
Write-Host "Build state: $(Join-Path $stateRoot "versions.json")" -ForegroundColor DarkGray