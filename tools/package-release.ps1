param(
    [string]$OutDir = 'out',
    [string]$Version = '',          # release tag, e.g. v1.0.0; empty = dev build
    [string]$Repo = $env:GITHUB_REPOSITORY,   # owner/name, enables self-update in the build
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$SkipPublish
)
# Builds a ready-to-run Detour zip: the app plus Xray, sing-box, geo files, the rule sets the
# connection rules use, and zapret. In Russia GitHub is often unreachable without a VPN, so a
# first start must not depend on downloading the cores.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root "$OutDir\Detour"
$bin = Join-Path $app 'bin'
$tmp = Join-Path $root "$OutDir\tmp"
New-Item -ItemType Directory -Force $tmp | Out-Null

$headers = @{ 'User-Agent' = 'detour-release' }
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

function Retry([scriptblock]$action) {
    for ($i = 1; ; $i++) {
        try { return & $action } catch { if ($i -ge 3) { throw }; Write-Host "  retry ${i}: $($_.Exception.Message)"; Start-Sleep -Seconds (5 * $i) }
    }
}
# Core versions and their SHA-256 are pinned in cores.lock.json (tools/update-cores-lock.ps1): a release
# never ships whatever "latest" happens to be, and a replaced asset fails the build.
$lock = Get-Content (Join-Path $PSScriptRoot 'cores.lock.json') -Raw | ConvertFrom-Json
function Pinned([string]$key) { $lock.$key }
function Fetch([string]$url, [string]$file, [int]$minSize = 1000, [string]$sha256 = '') {
    Write-Host "  $url"
    Retry { Invoke-WebRequest -Headers $headers -Uri $url -OutFile $file }
    if ((Get-Item $file).Length -lt $minSize) { throw "download too small: $url" }
    if ($sha256) {
        $got = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($got -ne $sha256) { throw "SHA-256 mismatch for ${url}: expected $sha256, got $got" }
    }
}
function FetchPinned([string]$key, [string]$asset, [string]$file) {
    $p = Pinned $key
    $sha = $p.sha256.$asset
    if (-not $sha) { throw "cores.lock.json: no hash for $key/$asset" }
    Fetch "https://github.com/$($p.repo)/releases/download/$($p.tag)/$asset" $file 1000 $sha
}
function Unzip-Into([string]$zip, [string]$dest) {
    $x = Join-Path $tmp ([IO.Path]::GetRandomFileName())
    Expand-Archive -Path $zip -DestinationPath $x -Force
    # Release zips may or may not have a top-level folder.
    $items = Get-ChildItem $x
    $src = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $x }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
}

$isWin = $Rid -eq 'win-x64'
# Windows: the WPF app. macOS/Linux: the Avalonia app (assembly "v2rayN", renamed to Detour below).
$project = if ($isWin) { 'v2rayN\v2rayN\v2rayN.csproj' } else { 'v2rayN\v2rayN.Desktop\v2rayN.Desktop.csproj' }
if (-not $SkipPublish) {
    Write-Host "Publishing Detour ($Rid)"
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    dotnet publish (Join-Path $root $project) -c Release -r $Rid --self-contained true -o $app `
        "-p:DetourVersion=$(if ($Version) { $Version.TrimStart('v') } else { '0.2.0' })" "-p:DetourRepo=$Repo"
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    if (-not $isWin) {
        Move-Item (Join-Path $app 'v2rayN') (Join-Path $app 'Detour') -Force
        Get-ChildItem $app -Filter '*.pdb' | Remove-Item -Force
    }
}
New-Item -ItemType Directory -Force $bin | Out-Null

# Core asset names per platform.
$xrAsset = @{ 'win-x64' = 'Xray-windows-64.zip'; 'linux-x64' = 'Xray-linux-64.zip'; 'linux-arm64' = 'Xray-linux-arm64-v8a.zip'
    'osx-x64' = 'Xray-macos-64.zip'; 'osx-arm64' = 'Xray-macos-arm64-v8a.zip' }[$Rid]
$sbSuffix = @{ 'win-x64' = 'windows-amd64.zip'; 'linux-x64' = 'linux-amd64.tar.gz'; 'linux-arm64' = 'linux-arm64.tar.gz'
    'osx-x64' = 'darwin-amd64.tar.gz'; 'osx-arm64' = 'darwin-arm64.tar.gz' }[$Rid]
$tpwsFolder = @{ 'linux-x64' = 'linux-x86_64'; 'linux-arm64' = 'linux-arm64'; 'osx-x64' = 'mac64'; 'osx-arm64' = 'mac64' }[$Rid]

function Untar-Into([string]$tgz, [string]$dest) {
    $x = Join-Path $tmp ([IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Force $x | Out-Null
    tar -xzf $tgz -C $x
    if ($LASTEXITCODE) { throw "tar failed: $tgz" }
    $items = Get-ChildItem $x
    $src = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $x }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
}

Write-Host 'Xray'
$xr = Pinned 'xray'
FetchPinned 'xray' $xrAsset "$tmp\xray.zip"
Unzip-Into "$tmp\xray.zip" "$bin\xray"

Write-Host 'sing-box'
$sb = Pinned 'singbox'
$sbVer = $sb.tag.TrimStart('v')
$sbFile = Join-Path $tmp ("sing-box." + $(if ($sbSuffix.EndsWith('.zip')) { 'zip' } else { 'tar.gz' }))
FetchPinned 'singbox' "sing-box-$sbVer-$sbSuffix" $sbFile
if ($sbSuffix.EndsWith('.zip')) { Unzip-Into $sbFile "$bin\sing_box" } else { Untar-Into $sbFile "$bin\sing_box" }

Write-Host 'Geo files'
foreach ($n in 'geoip', 'geosite') {
    Fetch "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/$n.dat" "$bin\$n.dat"
}

Write-Host 'Rule sets used by the connection rules'
New-Item -ItemType Directory -Force "$bin\srss" | Out-Null
foreach ($rs in @(@('geosite', 'category-ru'), @('geosite', 'private'), @('geoip', 'ru'), @('geoip', 'private'))) {
    Fetch "https://raw.githubusercontent.com/2dust/sing-box-rules/rule-set-$($rs[0])/$($rs[0])-$($rs[1]).srs" "$bin\srss\$($rs[0])-$($rs[1]).srs" 50
}

if ($isWin) {
    Write-Host 'zapret (Flowseal)'
    $zp = Pinned 'zapret'
    FetchPinned 'zapret' "zapret-discord-youtube-$($zp.tag).zip" "$tmp\zapret.zip"
    Unzip-Into "$tmp\zapret.zip" "$bin\zapret\current"
    if (-not (Test-Path "$bin\zapret\current\bin\winws.exe")) { throw 'winws.exe not found in the zapret release' }
    Set-Content -Path "$bin\zapret\current\.version" -Value $zp.tag -NoNewline -Encoding ascii
} else {
    Write-Host 'zapret (bol-van, tpws)'
    $zp = Pinned 'tpws'
    FetchPinned 'tpws' "zapret-$($zp.tag).zip" "$tmp\zapret.zip"
    $x = Join-Path $tmp 'zapret-src'
    Expand-Archive -Path "$tmp\zapret.zip" -DestinationPath $x -Force
    $tpws = Get-ChildItem $x -Recurse -File -Filter 'tpws' | Where-Object { $_.Directory.Name -eq $tpwsFolder } | Select-Object -First 1
    if (-not $tpws) { throw "binaries/$tpwsFolder/tpws not found in the zapret release" }
    New-Item -ItemType Directory -Force "$bin\zapret\tpws" | Out-Null
    Copy-Item $tpws.FullName "$bin\zapret\tpws\tpws" -Force
    Set-Content -Path "$bin\zapret\tpws\.version" -Value $zp.tag -NoNewline -Encoding ascii
}

if ($isWin) {
    # Emergency mode: Tor with Snowflake / WebTunnel (lyrebird). torproject.org is blocked in Russia, so it
    # can't be downloaded on first use: it ships in the archive, from the Expert Bundle pinned in the lock.
    Write-Host 'Tor (Expert Bundle)'
    $tor = Pinned 'tor'
    $torAsset = "tor-expert-bundle-windows-x86_64-$($tor.tag).tar.gz"
    $torSha = $tor.sha256.$torAsset
    if (-not $torSha) { throw "cores.lock.json: no hash for tor/$torAsset" }
    Fetch "$($tor.url)/$($tor.tag)/$torAsset" "$tmp\tor.tar.gz" 1000000 $torSha
    $x = Join-Path $tmp 'tor-src'
    New-Item -ItemType Directory -Force $x | Out-Null
    tar -xzf "$tmp\tor.tar.gz" -C $x
    if ($LASTEXITCODE) { throw 'tar failed: tor bundle' }
    New-Item -ItemType Directory -Force "$bin\tor" | Out-Null
    # Only what a client needs: tor itself and lyrebird (snowflake, webtunnel, obfs4). No geoip (26 MB, only
    # for country rules), no conjure-client, no tor-gencert.
    foreach ($f in 'tor\tor.exe', 'tor\pluggable_transports\lyrebird.exe', 'tor\pluggable_transports\pt_config.json') {
        if (-not (Test-Path "$x\$f")) { throw "tor bundle: $f not found" }
        Copy-Item "$x\$f" "$bin\tor\" -Force
    }
    Copy-Item "$x\docs\tor.txt" "$bin\tor\LICENSE-tor.txt" -Force
    Copy-Item "$x\docs\lyrebird.txt" "$bin\tor\LICENSE-lyrebird.txt" -Force
    Set-Content -Path "$bin\tor\.version" -Value $tor.tag -NoNewline -Encoding ascii
}

$readme = if ($isWin) { 'release-readme.txt' } else { 'release-readme-unix.txt' }
Copy-Item (Join-Path $PSScriptRoot $readme) (Join-Path $app $(if ($isWin) { 'ПРОЧТИ.txt' } else { 'README.txt' })) -Force

if ($isWin) {
    $name = if ($Version) { "Detour-$Version-win-x64.zip" } else { 'Detour-win-x64.zip' }
    $zip = Join-Path $root "$OutDir\$name"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "Zipping $name"
    Compress-Archive -Path $app -DestinationPath $zip
} else {
    # tar keeps the executable bits that zip would lose.
    foreach ($exe in @("$app/Detour", "$bin/xray/xray", "$bin/sing_box/sing-box", "$bin/zapret/tpws/tpws")) {
        if (Test-Path $exe) { chmod +x $exe }
    }
    $name = if ($Version) { "Detour-$Version-$Rid.tar.gz" } else { "Detour-$Rid.tar.gz" }
    $zip = Join-Path $root "$OutDir\$name"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "Packing $name"
    tar -czf $zip -C (Split-Path $app -Parent) (Split-Path $app -Leaf)
    if ($LASTEXITCODE) { throw 'tar failed' }
}
Remove-Item $tmp -Recurse -Force
# "<hash>  <name>", the format sha256sum -c and the release's SHA256SUMS.txt use.
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name`n")
Write-Host "Done: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Write-Host "Xray $($xr.tag), sing-box $($sb.tag), zapret $($zp.tag)$(if ($isWin) { ", Tor $((Pinned 'tor').tag)" })"
