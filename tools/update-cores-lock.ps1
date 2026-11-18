param(
    [string]$Xray = '',      # tag, empty = latest
    [string]$SingBox = '',
    [string]$Zapret = '',    # Flowseal/zapret-discord-youtube
    [string]$Tpws = ''       # bol-van/zapret
)
# Pins the cores a release ships: writes tools/cores.lock.json with each release tag and the SHA-256 of
# every asset package-release.ps1 downloads. Hashes come from GitHub's asset digests. Run it on purpose
# when moving to new core versions and review the diff: a release build then fails if an asset changed.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$headers = @{ 'User-Agent' = 'detour-release' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

function Release([string]$repo, [string]$tag) {
    $url = if ($tag) { "https://api.github.com/repos/$repo/releases/tags/$tag" } else { "https://api.github.com/repos/$repo/releases/latest" }
    Invoke-RestMethod -Headers $headers $url
}
function Pin([string]$repo, [string]$tag, [string[]]$names) {
    $r = Release $repo $tag
    $hashes = [ordered]@{}
    foreach ($n in $names) {
        $name = $n.Replace('{tag}', $r.tag_name).Replace('{ver}', $r.tag_name.TrimStart('v'))
        $a = $r.assets | Where-Object name -EQ $name
        if (-not $a) { throw "$repo $($r.tag_name): no asset $name" }
        if ($a.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "$repo $($r.tag_name): no digest for $name" }
        $hashes[$name] = $Matches[1]
    }
    [ordered]@{ repo = $repo; tag = $r.tag_name; sha256 = $hashes }
}

$lock = [ordered]@{
    xray = Pin 'XTLS/Xray-core' $Xray @('Xray-windows-64.zip', 'Xray-linux-64.zip', 'Xray-linux-arm64-v8a.zip', 'Xray-macos-64.zip', 'Xray-macos-arm64-v8a.zip')
    singbox = Pin 'SagerNet/sing-box' $SingBox @('sing-box-{ver}-windows-amd64.zip', 'sing-box-{ver}-linux-amd64.tar.gz', 'sing-box-{ver}-linux-arm64.tar.gz', 'sing-box-{ver}-darwin-amd64.tar.gz', 'sing-box-{ver}-darwin-arm64.tar.gz')
    zapret = Pin 'Flowseal/zapret-discord-youtube' $Zapret @('zapret-discord-youtube-{tag}.zip')
    tpws = Pin 'bol-van/zapret' $Tpws @('zapret-{tag}.zip')
}
$path = Join-Path $PSScriptRoot 'cores.lock.json'
[IO.File]::WriteAllText($path, ($lock | ConvertTo-Json -Depth 5) + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Pinned: Xray $($lock.xray.tag), sing-box $($lock.singbox.tag), zapret $($lock.zapret.tag), tpws $($lock.tpws.tag) -> $path"
