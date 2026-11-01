<#
.SYNOPSIS
    Refreshes the built-in manufacturer list (Assets\oui.tsv) from the IEEE's MA-L registry, so
    every release ships a snapshot as new as the release itself. Run by Publish-Release.ps1;
    the app refreshes its own copy daily from the same source (see OuiRegistry).

.DESCRIPTION
    Downloads https://standards-oui.ieee.org/oui/oui.csv (about 4 MB), keeps only the assignment
    (six hex digits) and the organisation name, and writes them as one tab-separated line each,
    sorted - about 1 MB, which the single-file publish compresses to a few hundred KB.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path "Assets\oui.tsv")
)

$ErrorActionPreference = "Stop"
$url = "https://standards-oui.ieee.org/oui/oui.csv"
$temp = Join-Path $env:TEMP "oui-download.csv"

Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing -TimeoutSec 120 -UserAgent "InternetMonitor-release (+https://github.com/DutchCoban/InternetMonitor)"
$rows = Import-Csv $temp
if ($rows.Count -lt 20000) { throw "Only $($rows.Count) rows in the IEEE list - not the registry" }

$lines = foreach ($row in $rows) {
    $oui = ($row.Assignment -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $name = ($row.'Organization Name' -replace '\s+', ' ').Trim()
    if ($oui.Length -eq 6 -and $name.Length -gt 0) { "$oui`t$name" }
}
$lines = $lines | Sort-Object -Unique
[IO.File]::WriteAllLines($Output, $lines, (New-Object Text.UTF8Encoding $false))
Remove-Item $temp
Write-Host "$($lines.Count) manufacturers written to $Output"
