param(
    [ValidateSet("leadflow")]
    [string]$Target = "leadflow",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$publishRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $publishRoot "..")
$distRoot = Join-Path $repoRoot "dist"
$workRoot = Join-Path $publishRoot "tmp\build"
$packageName = if ($Runtime -eq "win-x64") { "LeadFlow-Windows-x64-$Configuration" } else { "LeadFlow-$Runtime-$Configuration" }
$layoutRoot = Join-Path $distRoot $packageName
$zipPath = Join-Path $distRoot "$packageName.zip"

function Invoke-DotnetPublish {
    param(
        [string]$ProjectPath,
        [string]$OutputPath,
        [string]$RuntimeName,
        [string]$Config
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
        "--self-contained", "false",
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
    Write-Host "== Building LeadFlow ($Configuration / $Runtime) ==" -ForegroundColor Cyan

    if ($Clean) {
        Remove-Item -LiteralPath $layoutRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    }

    New-Item -Path $distRoot -ItemType Directory -Force | Out-Null
    New-Item -Path $workRoot -ItemType Directory -Force | Out-Null

    $docsFolderName = Get-UnicodeName @(0x0414, 0x043E, 0x043A, 0x0443, 0x043C, 0x0435, 0x043D, 0x0442, 0x0430, 0x0446, 0x0438, 0x044F)
    $startFileName = Get-UnicodeName @(0x041A, 0x0430, 0x043A, 0x0020, 0x0437, 0x0430, 0x043F, 0x0443, 0x0441, 0x0442, 0x0438, 0x0442, 0x044C) + ".txt"

    $appOut = Join-Path $layoutRoot "LeadFlow"
    $docsOut = Join-Path $layoutRoot $docsFolderName
    $projectPath = Join-Path $repoRoot "LeadFlow\LeadFlow.csproj"

    if (-not (Test-Path $projectPath)) {
        throw "LeadFlow project not found: $projectPath"
    }

    Remove-Item -LiteralPath $layoutRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -Path $appOut -ItemType Directory -Force | Out-Null
    New-Item -Path $docsOut -ItemType Directory -Force | Out-Null

    Invoke-DotnetPublish -ProjectPath $projectPath -OutputPath $appOut -RuntimeName $Runtime -Config $Configuration

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

    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $layoutRoot,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "LeadFlow package ready." -ForegroundColor Green
    Write-Host "Folder: $layoutRoot"
    Write-Host "Archive: $zipPath ($zipMb MB)"
}

switch ($Target) {
    "leadflow" { Build-LeadFlowZip }
    default { throw "Unknown build target: $Target" }
}