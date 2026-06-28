param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath
)

$ErrorActionPreference = "Stop"

$items = @(
    [pscustomobject]@{ Type = "target"; Value = "leadflow"; Label = "LeadFlow (Windows x64 ZIP)" },
    [pscustomobject]@{ Type = "target"; Value = "orbita-worker"; Label = "Orbita Worker (Windows x64 MSI)" },
    [pscustomobject]@{ Type = "target"; Value = "all"; Label = "All targets" },
    [pscustomobject]@{ Type = "exit"; Value = "0"; Label = "Exit" }
)

$selected = New-Object "System.Collections.Generic.HashSet[string]"
$currentIndex = 0

function Write-MenuLine {
    param(
        [string]$Text,
        [bool]$IsCurrent,
        [bool]$IsSelected
    )

    $prefix = if ($IsCurrent) { ">" } else { " " }
    $marker = if ($IsSelected) { "[x]" } else { "[ ]" }
    $color = if ($IsCurrent) { "Yellow" } elseif ($IsSelected) { "Green" } else { "Gray" }
    Write-Host (" {0} {1} {2}" -f $prefix, $marker, $Text) -ForegroundColor $color
}

function Draw-Menu {
    Clear-Host
    Write-Host "========================================" -ForegroundColor DarkGray
    Write-Host "  LeadFlow / Orbita build" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor DarkGray
    Write-Host "  Up/Down: move   Space: toggle   Enter: build   Esc: exit" -ForegroundColor DarkGray
    Write-Host ""

    for ($i = 0; $i -lt $items.Count; $i++) {
        $item = $items[$i]
        $isCurrent = $i -eq $currentIndex
        $isSelected = $item.Type -eq "target" -and $selected.Contains($item.Value)
        Write-MenuLine -Text $item.Label -IsCurrent $isCurrent -IsSelected $isSelected
    }

    Write-Host ""
    if ($selected.Count -gt 0) {
        $selectedLabels = $items |
            Where-Object { $_.Type -eq "target" -and $selected.Contains($_.Value) } |
            ForEach-Object { $_.Label }
        Write-Host ("  Selected: {0}" -f ($selectedLabels -join ", ")) -ForegroundColor Green
    }
    else {
        Write-Host "  No targets selected. Enter runs the current item." -ForegroundColor DarkGray
    }
}

function Save-Result {
    param([string]$Value)
    Set-Content -LiteralPath $ResultPath -Value $Value -Encoding Ascii
}

while ($true) {
    Draw-Menu
    $key = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

    switch ($key.VirtualKeyCode) {
        38 { if ($currentIndex -gt 0) { $currentIndex-- } else { $currentIndex = $items.Count - 1 } }
        40 { if ($currentIndex -lt ($items.Count - 1)) { $currentIndex++ } else { $currentIndex = 0 } }
        32 {
            $item = $items[$currentIndex]
            if ($item.Type -eq "target") {
                if ($selected.Contains($item.Value)) {
                    [void]$selected.Remove($item.Value)
                }
                else {
                    [void]$selected.Add($item.Value)
                }
            }
        }
        13 {
            $item = $items[$currentIndex]

            if ($item.Type -eq "exit") {
                Save-Result -Value $item.Value
                return
            }

            if ($selected.Count -gt 0) {
                $ordered = $items |
                    Where-Object { $_.Type -eq "target" -and $selected.Contains($_.Value) } |
                    ForEach-Object { $_.Value }
                Save-Result -Value ($ordered -join ",")
                return
            }

            Save-Result -Value $item.Value
            return
        }
        27 {
            Save-Result -Value "0"
            return
        }
    }
}