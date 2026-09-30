<#
.SYNOPSIS
  Publishes what tools\publish.ps1 built: a GitHub release with every file in dist\upload, and the download page
  (dist\site) on GitHub Pages. Needs the GitHub CLI (gh) signed in with push rights to the repository.

.DESCRIPTION
  Installed copies look for updates at <repo>/releases/latest/download/, so the new release becomes the update as
  soon as it is published. The page goes to the gh-pages branch as a single fresh commit (it holds no binaries: the
  download links point at the latest release), so that branch never grows.

.EXAMPLE
  tools\release-github.ps1                                # version from Skypeek.Desktop.csproj
  tools\release-github.ps1 -Domain downloads.example.com  # also serve the page on your own domain
  tools\release-github.ps1 -PageOnly                      # redeploy only the download page
#>
param(
    [string]$Version,
    [string]$Repo = 'nickolsky/skypeek',
    # Custom domain for the page (its DNS needs a CNAME record pointing at <owner>.github.io).
    [string]$Domain,
    # Markdown release notes; without it GitHub lists the commits since the previous release.
    [string]$NotesFile,
    [switch]$PageOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$upload = Join-Path $root 'dist\upload'
$site = Join-Path $root 'dist\site'
$project = Join-Path $root 'src\Skypeek.Desktop\Skypeek.Desktop.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$email = '2866033+nickolsky@users.noreply.github.com'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

if (-not $PageOnly) {
    # Only publish what the app will accept: every feed signed with a key it trusts, and listing this version.
    $feeds = Get-ChildItem $upload -Filter 'releases.*.json'
    if (-not $feeds) { throw "No update feeds in $upload; run tools\publish.ps1 first." }
    $releaseTool = Join-Path $root 'tools\Skypeek.ReleaseTool\bin\Release\net10.0\skypeek-release.dll'
    foreach ($feed in $feeds) {
        Invoke-Checked "signature check of $($feed.Name)" { dotnet $releaseTool verify $feed.FullName }
        if (-not ((Get-Content $feed.FullName -Raw) -match [regex]::Escape("""Version"":""$Version"""))) {
            throw "$($feed.Name) does not list version $Version (built for another version?)"
        }
    }
    $notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
    Invoke-Checked 'gh release create' {
        gh release create "v$Version" (Get-ChildItem $upload -File).FullName --repo $Repo --title "Skypeek $Version" @notes
    }
}

# Download page: one commit on an orphan gh-pages branch, force-pushed.
$work = Join-Path ([IO.Path]::GetTempPath()) "skypeek-pages-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null
try {
    Copy-Item (Join-Path $site '*') $work -Recurse
    New-Item -ItemType File (Join-Path $work '.nojekyll') | Out-Null
    if ($Domain) { Set-Content (Join-Path $work 'CNAME') $Domain -NoNewline }
    Push-Location $work
    try {
        Invoke-Checked 'git init' { git init -q -b gh-pages }
        Invoke-Checked 'git add' { git add -A }
        Invoke-Checked 'git commit' { git -c user.name='Artem Nickolsky' -c user.email=$email commit -q -m "Download page for Skypeek $Version" }
        Invoke-Checked 'git push' {
            git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push -q --force "https://github.com/$Repo.git" gh-pages
        }
    }
    finally { Pop-Location }
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }

# Turn Pages on the first time; set the custom domain when given.
gh api "repos/$Repo/pages" --silent 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'enable GitHub Pages' { gh api -X POST "repos/$Repo/pages" -f 'source[branch]=gh-pages' -f 'source[path]=/' --silent }
}
if ($Domain) {
    Invoke-Checked 'set the Pages domain' { gh api -X PUT "repos/$Repo/pages" -f "cname=$Domain" --silent }
}
$owner, $name = $Repo.Split('/')
Write-Host "Release:       https://github.com/$Repo/releases/tag/v$Version"
Write-Host "Download page: $(if ($Domain) { "https://$Domain/" } else { "https://$owner.github.io/$name/" })"
