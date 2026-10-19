<#
.SYNOPSIS
  Regenerates every screenshot the README embeds (docs/screenshots), from a Debug build of the
  app over a synthetic month of data. Part of every release: the pictures must show the version
  being shipped.

.DESCRIPTION
  Steps: build Debug (unless -SkipBuild) -> seed three data directories with seed-data.py (the
  same month; one with the example endpoints off for the live pages, one dark and Dutch) ->
  capture each page with capture.ps1 -> export the provider report with the app's own --export
  and render it with Edge headless -> finish.py crops, pixelates the live values and writes
  docs/screenshots.

  Needs: Windows with the primary screen at least 1250x950, Python 3 with Pillow
  (py -3 -m pip install pillow), Microsoft Edge, and no Internet Monitor running (the app is
  single-instance per session). The live pages (Status, Diagnostics, Tools) show this machine's
  connection; the traceroute and speed test run for real. Takes about six minutes.

.PARAMETER Work
  Where the raw captures go (default: %TEMP%\im-screenshots). Inspect them when a shot is off.

.PARAMETER SkipCapture
  Reuse the captures already in -Work and only redo the report and the finishing step - for
  adjusting crops and blur boxes in finish.py without another six minutes of shooting.

.PARAMETER Only
  Recapture just these pictures (names as in the Shot list below, e.g. -Only history,mtu) over
  the data already in -Work, then finish. For fixing one shot whose clicks missed.
#>
param(
  [string]$Work = (Join-Path $env:TEMP "im-screenshots"),
  [switch]$SkipBuild,
  [switch]$SkipCapture,
  [string[]]$Only = @()
)
$ErrorActionPreference = "Stop"
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })   # "-Only a,b" arrives as one string from a command line
trap { Write-Host "FAILED: $_" -ForegroundColor Red; exit 1 }
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$exe = Join-Path $root "src\InternetMonitor\bin\Debug\net10.0-windows\InternetMonitor.exe"
$docs = Join-Path $root "docs\screenshots"
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$capture = Join-Path $PSScriptRoot "capture.ps1"

if (Get-Process InternetMonitor -ErrorAction SilentlyContinue) { throw "Internet Monitor is running - close it first (the app is single-instance)." }
if (-not (Test-Path $edge)) { throw "Microsoft Edge not found at $edge (needed to render the provider report)." }
py -3 -c "import PIL" 2>$null; if ($LASTEXITCODE -ne 0) { throw "Python 3 with Pillow is needed: py -3 -m pip install pillow" }

if (-not $SkipBuild) {
  Write-Host "Building Debug..."
  dotnet build (Join-Path $root "src\InternetMonitor\InternetMonitorV2.csproj") -c Debug -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$seed = Join-Path $PSScriptRoot "seed-data.py"
$data = Join-Path $Work "data"; $live = Join-Path $Work "data-live"; $dark = Join-Path $Work "data-dark"
if ($SkipCapture -or $Only.Count -gt 0) {
  if (-not (Test-Path (Join-Path $Work "status.png"))) { throw "-SkipCapture and -Only need captures in $Work" }
} else {
  if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
  New-Item -ItemType Directory $Work | Out-Null
  Write-Host "Seeding..."
  py -3 $seed $data | Out-Null
  py -3 $seed $live --no-endpoints | Out-Null
  py -3 $seed $dark --no-endpoints --theme Dark --language nl | Out-Null
}

function Shot([string]$Name, [string]$DataDir, [hashtable]$Opts) {
  if ($Only.Count -gt 0 -and $Only -notcontains $Name) { return }
  $a = @{ Exe = $exe; Data = $DataDir; Out = (Join-Path $Work "$Name.png") } + $Opts
  & $capture @a
  if (-not (Test-Path $a.Out)) { throw "No capture for $Name" }
}

if (-not $SkipCapture) {
Write-Host "Capturing..."
# Chip and row positions are window-image coordinates at 1164x852; see capture.ps1.
Shot "status"                  $live @{ Page = "status" }
Shot "status-dark"             $dark @{ Page = "status" }
Shot "diagnostics"             $live @{ Page = "diagnostics"; Wait = 20; Clicks = "318,720" }
Shot "incidents"               $data @{ Page = "incidents" }
Shot "incident-outage"         $data @{ Page = "incidents"; Clicks = "347,510" }
Shot "incident-network-change" $data @{ Page = "incidents"; Clicks = "396,456" }
Shot "incident-packet-loss"    $data @{ Page = "incidents"; Clicks = "367,618" }
Shot "history"                 $data @{ Page = "history"; Wait = 14; Clicks = "406,216;437,276;389,332" }
Shot "history-wifi"            $data @{ Page = "history"; Wait = 14; Clicks = "300,216;473,276;456,332" }
Shot "history-traffic"         $data @{ Page = "history"; Wait = 14; Clicks = "614,216;456,332" }
Shot "history-packet-loss"     $data @{ Page = "history"; Wait = 14; Clicks = "512,216;521,332" }
Shot "history-speedtest"       $data @{ Page = "history"; Wait = 14; Clicks = "715,216;571,332" }
Shot "reports"                 $data @{ Page = "reports" }
Shot "settings-network"        $data @{ Page = "settings"; Drag = "1148,250,1148,670" }   # drags the scrollbar thumb down to the network cards
Shot "traceroute"              $live @{ Page = "tools"; Wait = 35; ExtraArgs = "--trace 9.9.9.9" }
Shot "speedtest"               $live @{ Page = "tools"; Clicks = "528,132;297,234,1;1100,950"; AfterClickWait = 15000 }
Shot "mtu"                     $live @{ Page = "tools"; Clicks = "625,132;545,234,1;1100,950"; AfterClickWait = 6000 }
}

Write-Host "Provider report..."
$env:INTERNETMONITOR_DATA_DIR = $data
& $exe --export report (Join-Path $Work "provider-report.html")
if ($LASTEXITCODE -ne 0) { throw "Report export failed" }
# Start-Process rather than a direct call: Edge writes to stderr even when it succeeds, and under
# $ErrorActionPreference = "Stop" a redirected native stderr line becomes a terminating error.
$url = "file:///" + (Join-Path $Work "provider-report.html").Replace("\", "/")
$edgeArgs = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=$(Join-Path $Work 'edge-profile')", "--window-size=980,1080", "--screenshot=$(Join-Path $Work 'provider-report.png')", $url)
$render = Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -PassThru -NoNewWindow
if ($render.ExitCode -ne 0 -or -not (Test-Path (Join-Path $Work "provider-report.png"))) { throw "Edge did not render the report" }

Write-Host "Writing $docs..."
py -3 (Join-Path $PSScriptRoot "finish.py") $Work $docs
if ($LASTEXITCODE -ne 0) { throw "finish.py failed" }
Write-Host "Done. Check the pictures, then commit docs/screenshots."
