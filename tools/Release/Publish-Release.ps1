<#
.SYNOPSIS
    Builds a release the way the README's "Releasing" checklist says, in one go: tests (three times
    green), publish, sign, verify, self-test, VirusTotal - and writes the VirusTotal verdict into the
    release notes. Stops at the first step that fails.

.DESCRIPTION
    Everything here already existed as separate commands (dotnet publish, tools/ReleaseSigner,
    InternetMonitor.exe --selftest); this script only runs them in the right order with the right
    arguments, so a release cannot skip a step or get one wrong. Uploading the executable to
    VirusTotal shares it with VirusTotal and its partners, which is fine for a file about to be
    public; use -SkipVirusTotal for a build that is not.

    Not done here, on purpose: refreshing the screenshots (tools\Screenshots\Make-Screenshots.ps1
    drives the live app and wants a look afterwards), committing, pushing and the GitHub release.

.PARAMETER Output
    Where the executable, its signature and the release notes go. Default: publish-v2 in the repo.

.PARAMETER KeyDir
    Folder with release-signing-key.pem and virustotal-api-key.txt. Default: ~\.internetmonitor.

.PARAMETER SkipTests
    Do not run the test suite first.

.PARAMETER SkipVirusTotal
    Publish, sign and self-test only; the release notes keep their placeholder.

.EXAMPLE
    .\tools\Release\Publish-Release.ps1
#>
[CmdletBinding()]
param(
    [string]$Output = "",
    [string]$KeyDir = (Join-Path $HOME ".internetmonitor"),
    [switch]$SkipTests,
    [switch]$SkipVirusTotal
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ($Output -eq "") { $Output = Join-Path $repo "publish-v2" }
$project = Join-Path $repo "src\InternetMonitor\InternetMonitor.csproj"
$tests = Join-Path $repo "tests\InternetMonitor.Tests\InternetMonitor.Tests.csproj"
$signer = Join-Path $repo "tools\ReleaseSigner\ReleaseSigner.csproj"
$exe = Join-Path $Output "InternetMonitor.exe"

function Step($title) { Write-Host ""; Write-Host "== $title" -ForegroundColor Cyan }
function Fail($message) { Write-Host "FAILED: $message" -ForegroundColor Red; exit 1 }

# The version the self-update will compare with the tag; also names the release notes.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "No <Version> in $project" }
$notes = Join-Path $Output "release-notes-v$version.md"
Write-Host "Release v$version -> $Output"

if (-not $SkipTests) {
    Step "Tests, three times"
    dotnet build $tests -c Debug -nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "build" }
    foreach ($run in 1..3) {
        dotnet test $tests -c Debug --no-build -nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "test run $run" }
    }
}

Step "Publish"
# A running copy of the previous publish locks the file; the bundler's stack trace does not say so.
$running = Get-Process InternetMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { Fail "InternetMonitor.exe from $Output is running (PID $($running.Id -join ', ')) - close it (tray menu, Exit) and run again" }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $Output -nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "publish" }
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if (-not $fileVersion.StartsWith("$version.")) { Fail "published file version $fileVersion is not $version" }
Write-Host "$exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, file version $fileVersion)"

Step "Sign and verify"
$privateKey = Join-Path $KeyDir "release-signing-key.pem"
if (-not (Test-Path $privateKey)) { Fail "no signing key at $privateKey" }
dotnet run --project $signer -c Release -- sign $privateKey $exe
if ($LASTEXITCODE -ne 0) { Fail "sign" }
$publicKey = (Select-String -Path (Join-Path $repo "src\InternetMonitor\Core\Updates\ReleaseSignature.cs") -Pattern '"([A-Za-z0-9+/=]{80,})"').Matches[0].Groups[1].Value
$verdict = dotnet run --project $signer -c Release -- verify $publicKey $exe
if ($LASTEXITCODE -ne 0 -or ($verdict | Select-Object -Last 1) -ne "valid") { Fail "signature verify: $verdict" }
Write-Host "signature valid"

Step "Self-test of the published executable"
$selfTest = Join-Path $env:TEMP "internetmonitor-selftest-v$version.txt"
if (Test-Path $selfTest) { Remove-Item $selfTest }
# A windowed executable returns to the shell at once; Start-Process -Wait is what waits for it.
$process = Start-Process -FilePath $exe -ArgumentList "--selftest", "`"$selfTest`"" -Wait -PassThru -NoNewWindow
if ($process.ExitCode -ne 0) { Fail "self-test exit code $($process.ExitCode) (see $selfTest)" }
if (-not (Test-Path $selfTest)) { Fail "self-test wrote no transcript to $selfTest" }
$failed = Select-String -Path $selfTest -Pattern '^\d+\. \w+\s+Failed' | Select-Object -First 1
if ($failed) { Fail "self-test: $($failed.Line)" }
Write-Host "all steps ok ($selfTest)"

if ($SkipVirusTotal) {
    Step "VirusTotal skipped"
}
else {
    Step "VirusTotal"
    $apiKey = Join-Path $KeyDir "virustotal-api-key.txt"
    if (-not (Test-Path $apiKey)) { Fail "no VirusTotal API key at $apiKey" }
    $vt = dotnet run --project $signer -c Release -- virustotal $apiKey $exe
    $vt | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Fail "VirusTotal exit code $LASTEXITCODE - look at the report before releasing" }
    $line = $vt | Where-Object { $_ -like "- **Checked on VirusTotal.**`*" } | Select-Object -Last 1
    if (-not $line) { Fail "no release-notes line in the VirusTotal output" }

    if (Test-Path $notes) {
        $content = Get-Content $notes -Raw -Encoding UTF8
        # '$' is the only character that means something in a -replace replacement string.
        if ($content -match '(?m)^- \*\*Checked on VirusTotal\.\*\*.*$') {
            $content = $content -replace '(?m)^- \*\*Checked on VirusTotal\.\*\*.*$', $line.Replace('$', '$$')
        }
        elseif ($content -match '(?m)^## Security\s*$') {
            $content = $content -replace '(?m)^## Security\s*$', ("## Security`r`n" + $line.Replace('$', '$$'))
        }
        else {
            $content = $content.TrimEnd() + "`r`n`r`n## Security`r`n" + $line + "`r`n"
        }
        [IO.File]::WriteAllText($notes, $content, (New-Object Text.UTF8Encoding $false))
        Write-Host "verdict written into $notes"
    }
    else {
        Write-Host "no release notes at $notes - paste this line into them:" -ForegroundColor Yellow
        Write-Host $line
    }
}

Step "Done"
Write-Host "Next: refresh the screenshots (tools\Screenshots\Make-Screenshots.ps1), commit, push master, create the GitHub release v$version with InternetMonitor.exe + InternetMonitor.exe.sig and the notes."
