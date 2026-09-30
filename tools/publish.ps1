<#
.SYNOPSIS
  Builds a Skypeek release into dist\ (run on Windows with the .NET 10 SDK and PowerShell 7).

.DESCRIPTION
  For each target it publishes one self-contained single-file build, then:
    - packs it with Velopack: the installer (Windows Setup.exe, Linux AppImage) and the update packages;
    - signs the update feed (releases.<channel>.json) with the release key (tools/Skypeek.ReleaseTool);
    - keeps the plain single file as a separate download (no install; it only announces new versions).
  Results:
    dist\upload\   every file for the GitHub release (flat; tools\release-github.ps1 uploads it)
    dist\site\     the download page (GitHub Pages)
    dist\releases\ Velopack's release history per channel, kept between runs so updates can be deltas
  macOS builds are made on a Mac: tools/package-macos.sh.

.EXAMPLE
  tools\publish.ps1                        # Windows and Linux, version from Skypeek.Desktop.csproj
  tools\publish.ps1 -Targets win -Version 1.2.1
  tools\publish.ps1 -SingleFileOnly        # just the single files, no installers or signing (local testing)
  tools\publish.ps1 -UpdateUrl https://downloads.example.com/latest   # feeds and packages hosted elsewhere
#>
param(
    [string[]]$Targets = @('win', 'linux'),
    [string]$Version,
    [switch]$SingleFileOnly,
    # Where the app looks for updates and the page links to (default: the project's, the latest GitHub release).
    [string]$UpdateUrl,
    [string]$DownloadPage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Skypeek.Desktop\Skypeek.Desktop.csproj'
$dist = Join-Path $root 'dist'
$upload = Join-Path $dist 'upload'
$site = Join-Path $dist 'site'
$assets = Join-Path $root 'src\Skypeek.Desktop\Assets'
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
# Not "Skypeek": Velopack installs to %LOCALAPPDATA%\<packId> and removes that folder on uninstall, while
# %LOCALAPPDATA%\Skypeek holds the vault.
$packId = 'SkypeekApp'
$title = 'Skypeek for AWS'

function Reset-Dir($path) {
    if (Test-Path $path) { Remove-Item -Recurse -Force $path }
    New-Item -ItemType Directory -Force $path | Out-Null
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# Update address and download page as the app is built with (the project's defaults unless overridden there).
$props = & dotnet msbuild $project -getProperty:SkypeekUpdateUrl -getProperty:SkypeekDownloadPage | ConvertFrom-Json
$updateUrl = $(if ($UpdateUrl) { $UpdateUrl } else { $props.Properties.SkypeekUpdateUrl }).TrimEnd('/')
$downloadPage = if ($DownloadPage) { $DownloadPage } else { $props.Properties.SkypeekDownloadPage }
$urlProps = @("-p:SkypeekUpdateUrl=$updateUrl", "-p:SkypeekDownloadPage=$downloadPage")

Reset-Dir $upload
Reset-Dir $site
Invoke-Checked 'dotnet tool restore' { dotnet tool restore --tool-manifest (Join-Path $root 'dotnet-tools.json') | Out-Null }
if (-not $SingleFileOnly) {
    if (-not (Get-ChildItem (Join-Path $root 'src\Skypeek.Desktop\UpdateKeys') -Filter *.pub.pem -ErrorAction SilentlyContinue)) {
        throw 'No update signing key yet: run  dotnet run --project tools/Skypeek.ReleaseTool -- keygen  once (see README, "Releasing").'
    }
    Invoke-Checked 'release tool build' { dotnet build (Join-Path $root 'tools\Skypeek.ReleaseTool') -c Release -v q -nologo | Out-Null }
}
$releaseTool = Join-Path $root 'tools\Skypeek.ReleaseTool\bin\Release\net10.0\skypeek-release.dll'

$targetInfo = @{
    win   = @{ Rid = 'win-x64'; Exe = 'Skypeek.exe'; Icon = 'Skypeek.ico'; Directive = @(); Extra = @() }
    linux = @{ Rid = 'linux-x64'; Exe = 'Skypeek'; Icon = 'Skypeek.png'; Directive = @('[linux]'); Extra = @('-p:Portable=true') }
}
$downloads = [ordered]@{}

foreach ($target in $Targets) {
    $t = $targetInfo[$target]
    if (-not $t) { throw "Unknown target '$target' (win, linux; macOS: tools/package-macos.sh on a Mac)" }
    $rid = $t.Rid
    $build = Join-Path $dist "build\$rid"
    Reset-Dir $build

    # The SelfContained property, not --self-contained: the switch's false value was not honored.
    Invoke-Checked "$rid publish" {
        dotnet publish $project -c Release -r $rid -p:SelfContained=true "-p:Version=$Version" @urlProps @($t.Extra) -o $build -v q -nologo
    }
    Get-ChildItem $build -Filter *.pdb | Remove-Item

    # The plain single file.
    if ($target -eq 'win') {
        $single = Join-Path $upload "Skypeek-$rid.exe"
        Copy-Item (Join-Path $build $t.Exe) $single
    }
    else {
        # A tarball with install.sh (Windows tar cannot store the executable bit; install.sh sets it).
        $name = "skypeek-$rid"
        $stage = Join-Path $dist "stage\$name"
        Reset-Dir $stage
        Copy-Item (Join-Path $build $t.Exe) $stage
        Copy-Item (Join-Path $root 'packaging\linux\install.sh') $stage
        Copy-Item (Join-Path $assets 'Skypeek.png') (Join-Path $stage 'skypeek.png')
        $single = Join-Path $upload "Skypeek-$rid.tar.gz"
        # Windows' own bsdtar (a GNU tar from Git on PATH would read "D:" as a remote host).
        $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path $tarExe)) { $tarExe = 'tar' }
        Invoke-Checked 'tar' { & $tarExe -czf $single -C (Split-Path $stage) $name }
    }
    $downloads["$target-single"] = $single
    if ($SingleFileOnly) { continue }

    # Installer and update packages. The channel names the platform and CPU, so the packages of all platforms can
    # sit side by side in one GitHub release.
    $channel = $rid
    $releases = Join-Path $dist "releases\$channel"
    New-Item -ItemType Directory -Force $releases | Out-Null
    $packArgs = @('pack', '--packId', $packId, '--packVersion', $Version, '--packDir', $build, '--mainExe', $t.Exe,
        '--packTitle', $title, '--packAuthors', 'Artem Nickolsky', '--icon', (Join-Path $assets $t.Icon),
        '--channel', $channel, '--runtime', $rid, '--outputDir', $releases)
    Invoke-Checked "$rid pack" { dotnet vpk @($t.Directive) @packArgs }

    $feed = Join-Path $releases "releases.$channel.json"
    Invoke-Checked "$rid feed signing" { dotnet $releaseTool sign $feed }

    # This version's packages, the feed and its signature go to the release; the installer gets a friendlier name.
    Copy-Item $feed, "$feed.sig" $upload
    Get-ChildItem $releases -Filter "$packId-$Version-*.nupkg" | Copy-Item -Destination $upload
    if ($target -eq 'win') {
        $installer = Join-Path $upload "Skypeek-$rid-Setup.exe"
        Copy-Item (Get-ChildItem $releases -Filter "*-Setup.exe" | Select-Object -First 1).FullName $installer
    }
    else {
        $installer = Join-Path $upload "Skypeek-$rid.AppImage"
        Copy-Item (Get-ChildItem $releases -Filter "*.AppImage" | Select-Object -First 1).FullName $installer
    }
    $downloads["$target-installer"] = $installer
}

# Checksums for people who verify downloads by hand.
$sums = Get-ChildItem $upload -File | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
Set-Content (Join-Path $upload 'SHA256SUMS.txt') $sums

# Download page: links to the latest GitHub release, so it only changes when the layout does.
function Size($path) { if ($path -and (Test-Path $path)) { '{0:N0} MB' -f ((Get-Item $path).Length / 1MB) } else { '' } }
function Link($key) { if ($downloads[$key]) { "$updateUrl/$(Split-Path $downloads[$key] -Leaf)" } else { '' } }
$page = Get-Content (Join-Path $root 'packaging\site\index.html') -Raw
$values = @{
    VERSION = $Version; DOWNLOAD_BASE = $updateUrl; RELEASES_PAGE = $downloadPage
    WIN_INSTALLER = (Link 'win-installer'); WIN_INSTALLER_SIZE = (Size $downloads['win-installer'])
    WIN_SINGLE = (Link 'win-single'); WIN_SINGLE_SIZE = (Size $downloads['win-single'])
    LINUX_INSTALLER = (Link 'linux-installer'); LINUX_INSTALLER_SIZE = (Size $downloads['linux-installer'])
    LINUX_SINGLE = (Link 'linux-single'); LINUX_SINGLE_SIZE = (Size $downloads['linux-single'])
}
foreach ($key in $values.Keys) { $page = $page.Replace("{{$key}}", [string]$values[$key]) }
Set-Content (Join-Path $site 'index.html') $page -NoNewline
Copy-Item (Join-Path $assets 'Skypeek.png') (Join-Path $site 'skypeek.png')

Write-Host ""
Write-Host "Skypeek $Version"
Get-ChildItem $upload -File | ForEach-Object { Write-Host ("  {0,-40} {1,8}" -f $_.Name, (Size $_.FullName)) }
Write-Host "Release files: $upload"
Write-Host "Download page: $site\index.html"
