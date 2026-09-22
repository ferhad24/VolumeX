# VolumeX - one command to ship a new version.
#
#   .\build\release.ps1 1.1.0 "What changed"
#
# version -> engine build -> tests -> publish -> sign -> installer -> sign ->
# git commit + tag + push -> GitHub Release with VolumeX-Setup.exe.
# Every running copy sees the new version at its next update check and
# installs it with one click.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    # Multi-line release notes do not survive a command line; pass a file.
    [string]$NotesFile = "",
    # Builds and verifies dist\VolumeX-Setup.exe but commits, tags and
    # publishes nothing. The csproj version is restored afterwards.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$repo = "ferhad24/VolumeX"
$release = Join-Path $root "artifacts\release"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if ($NotesFile) { $Notes = Get-Content $NotesFile -Raw -Encoding UTF8 }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. Install it:  winget install JRSoftware.InnoSetup" }

$signer = "$env:LOCALAPPDATA\FerhadImza\imzala.ps1"
if (-not (Test-Path $signer)) { throw "Signing script not found: $signer (the updater only installs signed builds)" }

if (-not $DryRun) {
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "Not logged in to GitHub. Run once:  gh auth login" }
    if (git -C $root tag --list "v$Version") { throw "v$Version already exists." }
}

Write-Host "=== 1/7  Version $Version ===" -ForegroundColor Cyan
$csproj = Join-Path $root "src\Crescendo.App\Crescendo.App.csproj"
$csprojOriginal = Get-Content $csproj -Raw
$csprojOriginal -replace '<Version>[\d\.]+</Version>', "<Version>$Version</Version>" |
    Set-Content $csproj -NoNewline -Encoding UTF8

Write-Host "=== 2/7  Engine ===" -ForegroundColor Cyan
& cmd /c "`"$root\build\build-apo.cmd`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Engine build failed" }

Write-Host "=== 3/7  Tests ===" -ForegroundColor Cyan
& cmd /c "`"$root\build\run-tests.cmd`"" | Select-String "checks"
if ($LASTEXITCODE -ne 0) { throw "Tests failed - nothing was released" }

Write-Host "=== 4/7  Publish (self-contained, no .NET install needed) ===" -ForegroundColor Cyan
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Copy-Item (Join-Path $root "artifacts\CrescendoApo.dll") $release -Force

Write-Host "=== 5/7  Sign + installer ===" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File $signer $release | Out-Null
& $iscc "/DMyAppVersion=$Version" (Join-Path $root "installer\VolumeX.iss") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
$setup = Join-Path $root "dist\VolumeX-Setup.exe"
& powershell -NoProfile -ExecutionPolicy Bypass -File $signer $setup | Out-Null
if ((Get-AuthenticodeSignature $setup).SignerCertificate.Thumbprint -ne '21874BCDC82C01DA6B124E53C85A02F6C2815D36') {
    throw "Setup is not signed with the VolumeX certificate - the in-app updater would refuse it"
}

# Gate: run the updater's own signature check against this exact setup, plus
# its tampered/unsigned negatives. If the updater would refuse it, nobody gets it.
$updaterTests = Join-Path $root "tests\UpdaterTests"
dotnet build $updaterTests -v q --nologo | Out-Null
& (Join-Path $updaterTests "bin\Debug\net8.0-windows\UpdaterTests.exe") $setup
if ($LASTEXITCODE -ne 0) { throw "Updater signature tests failed - not releasing" }

if ($DryRun) {
    Set-Content $csproj $csprojOriginal -NoNewline -Encoding UTF8
    $mb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Dry run complete: $setup ($mb MB). Nothing was committed or published." -ForegroundColor Green
    return
}

Write-Host "=== 6/7  Git ===" -ForegroundColor Cyan
git -C $root add -A
# Through a file: Windows PowerShell splits a multi-line -m argument into
# pathspecs, the commit fails, and the tag then lands on the previous commit.
$commitFile = Join-Path $env:TEMP "volumex-commit.txt"
# No BOM: Windows PowerShell's UTF8 encoding writes one, and git keeps it in the subject.
[IO.File]::WriteAllText($commitFile,
    "Release $Version`n`n$Notes`n`nCo-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>",
    (New-Object Text.UTF8Encoding $false))
git -C $root commit -q -F $commitFile
if ($LASTEXITCODE -ne 0) { throw "Commit failed - not tagging" }
git -C $root tag "v$Version"
git -C $root push -q origin main "v$Version"

Write-Host "=== 7/7  GitHub Release ===" -ForegroundColor Cyan
if (-not $Notes) { $Notes = "VolumeX $Version" }
$notesFile = Join-Path $env:TEMP "volumex-notes.md"
$Notes | Set-Content $notesFile -Encoding UTF8
& gh release create "v$Version" $setup --repo $repo --title "VolumeX $Version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "GitHub release failed" }

Write-Host ""
Write-Host "Released v$Version." -ForegroundColor Green
Write-Host "Running copies will offer the update at their next check (on start, then every 6 hours)." -ForegroundColor Green
Write-Host "https://github.com/$repo/releases/latest"
