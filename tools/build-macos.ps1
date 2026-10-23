<#
.SYNOPSIS
  Builds the macOS release without a Mac: runs the release-mac GitHub workflow on the pushed commit and downloads its
  files (Apple Silicon and Intel) to dist\macos, where tools\publish.ps1 picks them up, signs their feeds and adds them
  to dist\upload. Needs the GitHub CLI (gh) signed in with rights to run workflows.

.EXAMPLE
  tools\build-macos.ps1                  # version from Skypeek.Desktop.csproj, current branch
  tools\build-macos.ps1 -Version 1.3.1
#>
param(
    [string]$Version,
    [string]$Repo = 'nickolsky/skypeek',
    [string]$Branch
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Skypeek.Desktop\Skypeek.Desktop.csproj'
$out = Join-Path $root 'dist\macos'
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
# This checkout only (no global safe.directory): the folder may be owned by another account, e.g. Administrators.
function Git { git -c "safe.directory=$($root.Replace('\', '/'))" -C $root @args }
if (-not $Branch) { $Branch = Git rev-parse --abbrev-ref HEAD }

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# The runner builds what is on GitHub, so that must be what is here.
Invoke-Checked 'git fetch' { Git fetch -q origin $Branch }
$local = Git rev-parse HEAD
$remote = Git rev-parse "origin/$Branch"
if ($local -ne $remote) { throw "HEAD ($($local.Substring(0, 7))) is not what origin/$Branch has ($($remote.Substring(0, 7))): push first." }

$started = [DateTime]::UtcNow.AddSeconds(-5)
Invoke-Checked 'gh workflow run' { gh workflow run release-mac.yml --repo $Repo --ref $Branch -f "version=$Version" }

# The new run shows up after a few seconds.
$run = $null
for ($i = 0; $i -lt 30 -and -not $run; $i++) {
    Start-Sleep -Seconds 3
    $run = gh run list --repo $Repo --workflow release-mac.yml --branch $Branch --event workflow_dispatch --limit 5 `
        --json databaseId,createdAt,headSha | ConvertFrom-Json |
        Where-Object { $_.headSha -eq $local -and ([DateTime]$_.createdAt).ToUniversalTime() -ge $started } | Select-Object -First 1
}
if (-not $run) { throw 'The release-mac run did not start (see the Actions tab).' }
Write-Host "release-mac run $($run.databaseId): https://github.com/$Repo/actions/runs/$($run.databaseId)"
Invoke-Checked 'release-mac run' { gh run watch $run.databaseId --repo $Repo --exit-status --interval 20 }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Invoke-Checked 'gh run download' { gh run download $run.databaseId --repo $Repo --dir $out }
$packages = Get-ChildItem $out -Recurse -Filter "SkypeekApp-$Version-osx-*-full.nupkg"
if (-not $packages) { throw "The downloaded files have no macOS packages for $Version." }
Write-Host ""
Get-ChildItem $out -Recurse -File | ForEach-Object { Write-Host ("  {0,-45} {1,8:N0} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host "macOS files: $out (tools\publish.ps1 signs and adds them)"
