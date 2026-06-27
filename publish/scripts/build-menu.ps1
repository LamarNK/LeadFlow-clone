param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath
)

$ErrorActionPreference = "Stop"

$items = @(
    [pscustomobject]@{ Type = "target"; Value = "leadflow"; Label = "LeadFlow (Windows x64 ZIP)" },
    [pscustomobject]@{ Type = "exit"; Value = "0"; Label = "Exit" }
)

$currentIndex = 0

function Write-MenuLine {
    param([string]$Text, [bool]$IsCurrent)
    $prefix = if ($IsCurrent) { ">" } else { " " }
    $color = if ($IsCurrent) { "Yellow" } else { "Gray" }
    Write-Host (" {0}  {1}" -f $prefix, $Text) -ForegroundColor $color
}

function Draw-Menu {
    Clear-Host
    Write-Host "========================================" -ForegroundColor DarkGray
    Write-Host "  LeadFlow build" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor DarkGray
    Write-Host "  Up/Down: move   Enter: build   Esc: exit" -ForegroundColor DarkGray
    Write-Host ""
    for ($i = 0; $i -lt $items.Count; $i++) {
        Write-MenuLine -Text $items[$i].Label -IsCurrent:($i -eq $currentIndex)
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
        13 {
            Save-Result -Value $items[$currentIndex].Value
            return
        }
        27 {
            Save-Result -Value "0"
            return
        }
    }
}