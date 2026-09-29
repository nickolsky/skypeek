<#
.SYNOPSIS
  Builds Skypeek for Windows and Linux into dist\ (run on Windows with the .NET 10 SDK).

.EXAMPLE
  tools\publish.ps1                 # Windows (framework-dependent single exe) and Linux (self-contained)
  tools\publish.ps1 -Targets win    # only Windows
  tools\publish.ps1 -SelfContained  # Windows exe that also runs without the .NET runtime installed

.NOTES
  dist\win-x64\Skypeek.exe                        copy anywhere and run
  dist\Skypeek-<version>-linux-x64.tar.gz         on Linux: tar xzf …; sh skypeek-linux-x64/install.sh
  macOS apps are built on a Mac: tools/package-macos.sh (signing and notarization need Apple's tools).
#>
param(
    [string[]]$Targets = @('win', 'linux'),
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Skypeek.Desktop\Skypeek.Desktop.csproj'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force $dist | Out-Null

if ($Targets -contains 'win') {
    $out = Join-Path $dist 'win-x64'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    # The SelfContained property, not --self-contained: the switch's false value was not honored.
    dotnet publish $project -c Release -r win-x64 "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())" -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    Get-ChildItem $out -Filter *.pdb | Remove-Item
    Write-Host "Windows: $out\Skypeek.exe"
}

if ($Targets -contains 'linux') {
    $name = 'skypeek-linux-x64'
    $stage = Join-Path $dist $name
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    # -p:Portable=true builds the cross-platform target (the Windows one adds Windows toasts).
    dotnet publish $project -c Release -r linux-x64 -p:SelfContained=true -p:Portable=true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed' }
    Get-ChildItem $stage -Filter *.pdb | Remove-Item
    Copy-Item (Join-Path $root 'packaging\linux\install.sh') $stage
    Copy-Item (Join-Path $root 'src\Skypeek.Desktop\Assets\Skypeek.png') (Join-Path $stage 'skypeek.png')
    $tar = Join-Path $dist "Skypeek-$version-linux-x64.tar.gz"
    if (Test-Path $tar) { Remove-Item $tar }
    # Windows tar cannot store the executable bit; install.sh sets it.
    # Windows' own bsdtar (a GNU tar from Git on PATH would read "D:" as a remote host).
    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tarExe)) { $tarExe = 'tar' }
    & $tarExe -czf $tar -C $dist $name
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
    Write-Host "Linux:   $tar"
}
