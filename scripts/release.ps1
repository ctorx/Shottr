<#
.SYNOPSIS
    Builds, tests and publishes a Shottr release on GitHub.

.DESCRIPTION
    Reads the version from src\Shottr\Shottr.csproj and releases it as tag v<version>.

    The script refuses to run when the working tree has uncommitted changes, when the
    current branch has commits that are not on origin, or when the tag already exists
    locally or on origin. It then builds the solution, runs the tests, publishes the
    self-contained Shottr.exe and writes a SHA256SUMS file next to it.

    Publishing is one "gh release create" command, which creates the tag and the release
    together, so a failure cannot leave a tag without a release.

.PARAMETER DryRun
    Runs every check and build step but publishes nothing.

.PARAMETER Notes
    Release notes text. Without it, GitHub generates notes from the commits and pull requests.

.EXAMPLE
    .\scripts\release.ps1 -DryRun
    .\scripts\release.ps1
    .\scripts\release.ps1 -Notes "Fixes the blank editor on remote desktops."
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Shottr\Shottr.csproj'
$solution = Join-Path $root 'Shottr.sln'
$publishDir = Join-Path $root 'publish'
$assetName = 'Shottr.exe'

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host "Release stopped: $text" -ForegroundColor Red; exit 1 }

# Runs a native command and stops the release if it fails.
function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Fail "$what failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    foreach ($tool in 'git', 'dotnet', 'gh') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool is not installed or not on PATH." }
    }

    # ---- 1. Version ----------------------------------------------------------------------

    $version = (& dotnet msbuild $project -getProperty:Version -nologo).Trim()
    if ($LASTEXITCODE -ne 0) { Fail 'Could not read the version from Shottr.csproj.' }
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        Fail "The version in Shottr.csproj is '$version'. It must be MAJOR.MINOR.PATCH, such as 1.4.0."
    }
    $tag = "v$version"
    Step "Releasing Shottr $version as tag $tag$(if ($DryRun) { ' (dry run)' })"

    # ---- 2. Repository checks ------------------------------------------------------------

    Step 'Checking the repository'
    $dirty = & git status --porcelain
    if ($dirty) { Fail "the working tree has uncommitted changes:`n$($dirty -join "`n")" }

    Invoke-Checked 'git fetch' { git fetch origin --quiet }
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -eq 'HEAD') { Fail 'HEAD is detached. Check out the branch you want to release.' }
    $upstream = & git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $upstream) { Fail "branch '$branch' has no upstream on origin. Push it with: git push -u origin $branch" }
    $ahead = [int](& git rev-list --count '@{u}..HEAD')
    if ($ahead -gt 0) { Fail "branch '$branch' has $ahead commit(s) that are not on $upstream. Push them first." }

    & git rev-parse --quiet --verify "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) { Fail "tag $tag already exists locally. Bump the version in Shottr.csproj." }
    $remoteTag = & git ls-remote --tags origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { Fail 'Could not list the tags on origin.' }
    if ($remoteTag) { Fail "tag $tag already exists on origin. Bump the version in Shottr.csproj." }

    $commit = (& git rev-parse HEAD).Trim()

    if (-not $DryRun) {
        & gh auth status *> $null
        if ($LASTEXITCODE -ne 0) { Fail 'the GitHub CLI is not signed in. Run: gh auth login' }
    }

    # ---- 3. Build and test ---------------------------------------------------------------

    Step 'Building'
    Invoke-Checked 'dotnet build' { dotnet build $solution -c Release -nologo }
    Step 'Running tests'
    Invoke-Checked 'dotnet test' { dotnet test $solution -c Release --no-build -nologo }

    # ---- 4. Assets -----------------------------------------------------------------------

    Step 'Publishing Shottr.exe'
    & (Join-Path $root 'publish.ps1') -NoInstall
    $exe = Join-Path $publishDir $assetName
    if (-not (Test-Path $exe)) { Fail "$exe was not produced." }

    $builtVersion = (Get-Item $exe).VersionInfo.ProductVersion
    if ($builtVersion -ne $version) { Fail "the built exe reports version '$builtVersion', not '$version'." }

    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = Join-Path $publishDir 'SHA256SUMS'
    # One "<sha256>  <filename>" line per asset, with Unix line endings and no byte order mark.
    [IO.File]::WriteAllText($sums, "$hash  $assetName`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "SHA256SUMS: $hash  $assetName"

    # ---- 5 and 6. Publish ----------------------------------------------------------------

    # Notes go through a file so quotes and line breaks survive on every PowerShell version.
    $notesFile = $null
    $ghArgs = @('release', 'create', $tag, $exe, $sums, '--target', $commit, '--title', "Shottr $version")
    if ($PSBoundParameters.ContainsKey('Notes')) {
        $notesFile = Join-Path ([IO.Path]::GetTempPath()) "shottr-notes-$tag.md"
        [IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding($false)))
        $ghArgs += @('--notes-file', $notesFile)
    } else {
        $ghArgs += '--generate-notes'
    }

    if ($DryRun) {
        Step 'Dry run finished. Nothing was published. This command would run:'
        Write-Host ('  gh ' + (($ghArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '))
        if ($notesFile) { Remove-Item $notesFile -ErrorAction SilentlyContinue }
        return
    }

    Step "Creating release $tag on GitHub"
    & gh @ghArgs
    $ghExit = $LASTEXITCODE
    if ($notesFile) { Remove-Item $notesFile -ErrorAction SilentlyContinue }

    if ($ghExit -ne 0) {
        Write-Host ''
        Write-Host "gh release create failed with exit code $ghExit. Checking what it created..." -ForegroundColor Red
        $tagNow = & git ls-remote --tags origin "refs/tags/$tag"
        $releaseNow = & gh release view $tag --json tagName,isDraft,assets --jq '"draft=\(.isDraft) assets=\([.assets[].name] | join(", "))"' 2>$null
        if ($LASTEXITCODE -eq 0 -and $releaseNow) {
            Write-Host "  A release for $tag exists on GitHub: $releaseNow" -ForegroundColor Yellow
            Write-Host "  Check its assets. To start over, run: gh release delete $tag --cleanup-tag --yes" -ForegroundColor Yellow
        } else {
            Write-Host "  No release for $tag exists on GitHub." -ForegroundColor Yellow
        }
        if ($tagNow) {
            Write-Host "  Tag $tag exists on origin. To remove it, run: git push origin :refs/tags/$tag" -ForegroundColor Yellow
        } else {
            Write-Host "  Tag $tag does not exist on origin. Nothing was created, so you can rerun this script." -ForegroundColor Yellow
        }
        exit 1
    }

    & git fetch origin --tags --quiet
    Step "Released Shottr $version"
} finally {
    Pop-Location
}
