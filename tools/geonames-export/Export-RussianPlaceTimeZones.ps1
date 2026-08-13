param(
    [Parameter(Mandatory = $true)]
    [string]$MainDumpPath,

    [Parameter(Mandatory = $true)]
    [string]$RussianAlternateNamesPath,

    [Parameter(Mandatory = $true)]
    [string]$Admin1CodesPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$timezoneOffsets = @{
    'Europe/Kaliningrad' = 120
    'Europe/Moscow' = 180
    'Europe/Kirov' = 180
    'Europe/Volgograd' = 180
    'Europe/Simferopol' = 180
    'Europe/Astrakhan' = 240
    'Europe/Samara' = 240
    'Europe/Saratov' = 240
    'Europe/Ulyanovsk' = 240
    'Asia/Yekaterinburg' = 300
    'Asia/Omsk' = 360
    'Asia/Barnaul' = 420
    'Asia/Novosibirsk' = 420
    'Asia/Novokuznetsk' = 420
    'Asia/Krasnoyarsk' = 420
    'Asia/Tomsk' = 420
    'Asia/Irkutsk' = 480
    'Asia/Chita' = 540
    'Asia/Yakutsk' = 540
    'Asia/Khandyga' = 540
    'Asia/Vladivostok' = 600
    'Asia/Ust-Nera' = 600
    'Asia/Magadan' = 660
    'Asia/Sakhalin' = 660
    'Asia/Kamchatka' = 720
    'Asia/Anadyr' = 720
}

$featureBonuses = @{
    'PPLC' = [long]2000000000000
    'PPLA' = [long]1500000000000
    'PPLA2' = [long]1000000000000
    'PPLA3' = [long]800000000000
    'PPLA4' = [long]700000000000
    'PPLS' = [long]100000000
}

function Normalize-PlaceName([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    $builder = [System.Text.StringBuilder]::new($Value.Length)
    $previousSpace = $true
    foreach ($raw in $Value.Trim().ToLowerInvariant().ToCharArray()) {
        $ch = if ([int][char]$raw -eq 0x451) { [char]0x435 } else { $raw }
        if ([char]::IsLetterOrDigit($ch)) {
            [void]$builder.Append($ch)
            $previousSpace = $false
        }
        elseif (-not $previousSpace) {
            [void]$builder.Append(' ')
            $previousSpace = $true
        }
    }

    return $builder.ToString().Trim()
}

function Test-HasCyrillic([string]$Value) {
    return $Value -match '\p{IsCyrillic}'
}

$places = @{}
$aliases = @{}
$ambiguousReplacements = 0

function Add-Alias([string]$Name, [int]$OffsetMinutes, [long]$Score) {
    $normalized = Normalize-PlaceName $Name
    if ($normalized.Length -lt 2) {
        return
    }

    $existing = $aliases[$normalized]
    if ($null -eq $existing -or $Score -gt $existing.Score) {
        if ($null -ne $existing -and $existing.OffsetMinutes -ne $OffsetMinutes) {
            $script:ambiguousReplacements++
        }

        $aliases[$normalized] = [pscustomobject]@{
            OffsetMinutes = $OffsetMinutes
            Score = $Score
        }
    }
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
$reader = [System.IO.StreamReader]::new((Resolve-Path $MainDumpPath), $utf8, $true, 1048576)
try {
    while (($line = $reader.ReadLine()) -ne $null) {
        $columns = $line.Split("`t")
        if ($columns.Length -lt 19 -or $columns[6] -ne 'P' -or @('PPLQ', 'PPLH', 'PPLW') -contains $columns[7]) {
            continue
        }

        $timezone = $columns[17]
        if (-not $timezoneOffsets.ContainsKey($timezone)) {
            continue
        }

        [long]$population = 0
        [void][long]::TryParse($columns[14], [ref]$population)
        $bonus = if ($featureBonuses.ContainsKey($columns[7])) { $featureBonuses[$columns[7]] } else { [long]0 }
        $place = [pscustomobject]@{
            OffsetMinutes = [int]$timezoneOffsets[$timezone]
            Admin1 = $columns[10]
            Score = $bonus + $population
        }
        $places[$columns[0]] = $place

        if (Test-HasCyrillic $columns[1]) {
            Add-Alias $columns[1] $place.OffsetMinutes $place.Score
        }
        foreach ($alternate in $columns[3].Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            if (Test-HasCyrillic $alternate) {
                Add-Alias $alternate $place.OffsetMinutes $place.Score
            }
        }
    }
}
finally {
    $reader.Dispose()
}

$admin1ToGeoNameId = @{}
$regionGeoNameIds = @{}
$reader = [System.IO.StreamReader]::new((Resolve-Path $Admin1CodesPath), $utf8, $true, 65536)
try {
    while (($line = $reader.ReadLine()) -ne $null) {
        $columns = $line.Split("`t")
        if ($columns.Length -lt 4 -or -not $columns[0].StartsWith('RU.', [System.StringComparison]::Ordinal)) {
            continue
        }

        $adminCode = $columns[0].Substring(3)
        $admin1ToGeoNameId[$adminCode] = $columns[3]
        $regionGeoNameIds[$columns[3]] = $adminCode
    }
}
finally {
    $reader.Dispose()
}

$regionAliases = @{}
foreach ($adminCode in $admin1ToGeoNameId.Keys) {
    $regionAliases[$adminCode] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
}

$reader = [System.IO.StreamReader]::new((Resolve-Path $RussianAlternateNamesPath), $utf8, $true, 1048576)
try {
    while (($line = $reader.ReadLine()) -ne $null) {
        $columns = $line.Split("`t")
        if ($columns.Length -lt 4 -or $columns[2] -ne 'ru' -or ($columns.Length -gt 7 -and $columns[7] -eq '1') -or ($columns.Length -gt 9 -and $columns[9].Length -gt 0)) {
            continue
        }

        $adminCode = $regionGeoNameIds[$columns[1]]
        if ($null -ne $adminCode) {
            $normalized = Normalize-PlaceName $columns[3]
            if ($normalized.Length -ge 2) {
                [void]$regionAliases[$adminCode].Add($normalized)
            }
        }
    }
}
finally {
    $reader.Dispose()
}

$reader = [System.IO.StreamReader]::new((Resolve-Path $RussianAlternateNamesPath), $utf8, $true, 1048576)
try {
    while (($line = $reader.ReadLine()) -ne $null) {
        $columns = $line.Split("`t")
        if ($columns.Length -lt 4 -or $columns[2] -ne 'ru' -or ($columns.Length -gt 7 -and $columns[7] -eq '1') -or ($columns.Length -gt 9 -and $columns[9].Length -gt 0)) {
            continue
        }

        $place = $places[$columns[1]]
        if ($null -eq $place) {
            continue
        }

        $cityAlias = Normalize-PlaceName $columns[3]
        if ($cityAlias.Length -lt 2) {
            continue
        }

        Add-Alias $cityAlias $place.OffsetMinutes $place.Score
        $regions = $regionAliases[$place.Admin1]
        if ($null -eq $regions) {
            continue
        }

        foreach ($regionAlias in $regions) {
            $compositeScore = [long]3000000000000 + $place.Score
            Add-Alias "$cityAlias $regionAlias" $place.OffsetMinutes $compositeScore
            Add-Alias "$regionAlias $cityAlias" $place.OffsetMinutes $compositeScore
        }
    }
}
finally {
    $reader.Dispose()
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFullPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$outputStream = [System.IO.File]::Create($outputFullPath)
try {
    $gzip = [System.IO.Compression.GZipStream]::new($outputStream, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $writer = [System.IO.StreamWriter]::new($gzip, $utf8, 1048576, $true)
        try {
            foreach ($name in ($aliases.Keys | Sort-Object)) {
                $writer.Write($name)
                $writer.Write("`t")
                $writer.WriteLine($aliases[$name].OffsetMinutes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $gzip.Dispose()
    }
}
finally {
    $outputStream.Dispose()
}

$outputInfo = Get-Item $outputFullPath
Write-Host "Active populated places: $($places.Count)"
Write-Host "Generated aliases: $($aliases.Count)"
Write-Host "Ambiguous aliases replaced by a more significant place: $ambiguousReplacements"
Write-Host "Output: $($outputInfo.FullName) ($($outputInfo.Length) bytes)"
