param(
    [Parameter(Mandatory)][string]$Tag,       # e.g. v0.3.9
    [string]$Repo = 'ParmesanY/Detour',
    [string]$KeyFile = '',
    [string]$FromDir = ''                     # CI artifacts already downloaded (gh run download), skips step 3
)
# Signs a published release on the maintainer's machine. Release assets can be replaced by anyone who gets
# write access to the repo, so they are not trusted on their own: what gets signed are the archives the
# "Build and test" workflow built from the commit the tag points at (that commit must be the one in your local
# checkout), and each must have exactly the SHA-256 GitHub reports for the release asset of the same name.
# Signing those bytes signs the release asset. Only the .sig files are uploaded. The release itself is never
# downloaded (GitHub's file storage is often throttled in Russia); the CI artifacts are, unless -FromDir points
# at a folder where you already have them. Needs gh (logged in) and the .NET 10 SDK.
# Not 'Stop': in PowerShell 5.1 that turns any stderr line of gh/git into a fatal error. Failures are checked by hand.
$ErrorActionPreference = 'Continue'
$dir = Join-Path ([IO.Path]::GetTempPath()) "detour-sign-$Tag"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
$ci = Join-Path $dir 'ci'
New-Item -ItemType Directory $ci | Out-Null

# gh calls fail now and then on a flaky connection (VPN, ISP resets): retry before giving up.
function Retry-Gh([string]$what, [scriptblock]$call) {
    for ($i = 1; $i -le 4; $i++) {
        $out = & $call
        if (-not $LASTEXITCODE) { return $out }
        Write-Host "  $what failed (try $i of 4)" -ForegroundColor Yellow
        Start-Sleep -Seconds (3 * $i)
    }
    throw "$what failed: check the connection to GitHub (VPN on/off) and run again"
}

Write-Host "1/5 Tag $Tag on GitHub"
$sha = Retry-Gh 'reading the tag' { gh api "repos/$Repo/commits/$Tag" --jq .sha }
if (-not $sha) { throw "tag $Tag not found on GitHub" }
$local = (git -C (Split-Path $PSScriptRoot -Parent) rev-parse "$Tag^{commit}" 2>$null)
if ($local -ne $sha) { throw "tag $Tag is $sha on GitHub but '$local' locally: fetch tags and check before signing" }

Write-Host "2/5 SHA-256 of the release assets (from the GitHub API)"
$releaseHash = @{}
$lines = Retry-Gh 'reading the release' { gh api "repos/$Repo/releases/tags/$Tag" --jq '.assets[] | [.name, (.digest // empty)] | @tsv' }
foreach ($line in $lines) {
    $p = $line -split "`t", 2
    if ($p.Count -eq 2 -and $p[1] -match '^sha256:([0-9a-f]{64})$') { $releaseHash[$p[0]] = $Matches[1] }
}
if ($releaseHash.Count -eq 0) { throw "release $Tag has no assets with a digest" }

if ($FromDir) {
    Write-Host "3/5 CI builds from $FromDir"
    $ciFiles = Get-ChildItem $FromDir -Recurse -File | Where-Object { $_.Name -match '^Detour-.*\.(zip|tar\.gz)$' }
} else {
    Write-Host "3/5 Downloading the CI builds of $sha (~500 MB)"
    $runs = Retry-Gh 'listing CI runs' { gh run list --repo $Repo --workflow 'Build and test' --commit $sha --json databaseId --jq '.[].databaseId' }
    foreach ($run in $runs) {
        $runDir = Join-Path $ci $run
        Write-Host "  run $run"
        for ($i = 1; $i -le 3; $i++) {
            if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
            gh run download $run --repo $Repo --dir $runDir 2>$null
            if (-not $LASTEXITCODE) { break }
            Write-Host "  download failed (try $i of 3)" -ForegroundColor Yellow
        }
    }
    $ciFiles = Get-ChildItem $ci -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^Detour-.*\.(zip|tar\.gz)$' }
}

$toSign = @()
foreach ($name in $releaseHash.Keys | Where-Object { $_ -match '^Detour-.*\.(zip|tar\.gz)$' }) {
    $f = $ciFiles | Where-Object Name -EQ $name | Select-Object -First 1
    if (-not $f) { Write-Warning "${name}: no CI build to compare with, not signed"; continue }
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $releaseHash[$name]) { throw "${name}: the release asset differs from the CI build of ${sha} (replaced?)" }
    Write-Host "  $name matches the CI build"
    $toSign += $f
}
if (-not $toSign) { throw 'nothing to sign' }

Write-Host "4/5 Signing $($toSign.Count) archives (asks for the key password)"
$signArgs = @('run', '--file', (Join-Path $PSScriptRoot 'detour-sign.cs'), '--', 'sign') + ($toSign | ForEach-Object FullName)
if ($KeyFile) { $signArgs += @('--key', $KeyFile) }
dotnet @signArgs
if ($LASTEXITCODE) { throw 'signing failed' }

$sigs = $toSign | ForEach-Object { "$($_.FullName).sig" }
Write-Host "5/5 Uploading the signatures"
Retry-Gh 'uploading the signatures' { gh release upload $Tag --repo $Repo --clobber @sigs } | Out-Null
if ($LASTEXITCODE) { throw 'gh release upload failed' }
Write-Host "Signed and uploaded $($sigs.Count) signatures for $Tag (checked against the CI build of $sha)"
Remove-Item $dir -Recurse -Force
