param(
    [string]$OutDir = 'out',
    [string]$Version = '',          # release tag, e.g. v1.0.0; empty = dev build
    [string]$Repo = $env:GITHUB_REPOSITORY,   # owner/name, enables self-update in the build
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
function Latest([string]$repo) {
    Retry { Invoke-RestMethod -Headers $headers "https://api.github.com/repos/$repo/releases/latest" }
}
function Fetch([string]$url, [string]$file, [int]$minSize = 1000) {
    Write-Host "  $url"
    Retry { Invoke-WebRequest -Headers $headers -Uri $url -OutFile $file }
    if ((Get-Item $file).Length -lt $minSize) { throw "download too small: $url" }
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

if (-not $SkipPublish) {
    Write-Host 'Publishing Detour'
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    dotnet publish (Join-Path $root 'v2rayN\v2rayN\v2rayN.csproj') -c Release -r win-x64 --self-contained true -o $app `
        "-p:DetourVersion=$(if ($Version) { $Version.TrimStart('v') } else { '0.1.0' })" "-p:DetourRepo=$Repo"
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
}
New-Item -ItemType Directory -Force $bin | Out-Null

Write-Host 'Xray'
$xr = Latest 'XTLS/Xray-core'
Fetch "https://github.com/XTLS/Xray-core/releases/download/$($xr.tag_name)/Xray-windows-64.zip" "$tmp\xray.zip"
Unzip-Into "$tmp\xray.zip" "$bin\xray"

Write-Host 'sing-box'
$sb = Latest 'SagerNet/sing-box'
$sbVer = $sb.tag_name.TrimStart('v')
Fetch "https://github.com/SagerNet/sing-box/releases/download/$($sb.tag_name)/sing-box-$sbVer-windows-amd64.zip" "$tmp\sing-box.zip"
Unzip-Into "$tmp\sing-box.zip" "$bin\sing_box"

Write-Host 'Geo files'
foreach ($n in 'geoip', 'geosite') {
    Fetch "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/$n.dat" "$bin\$n.dat"
}

Write-Host 'Rule sets used by the connection rules'
New-Item -ItemType Directory -Force "$bin\srss" | Out-Null
foreach ($rs in @(@('geosite', 'category-ru'), @('geosite', 'private'), @('geoip', 'ru'), @('geoip', 'private'))) {
    Fetch "https://raw.githubusercontent.com/2dust/sing-box-rules/rule-set-$($rs[0])/$($rs[0])-$($rs[1]).srs" "$bin\srss\$($rs[0])-$($rs[1]).srs" 50
}

Write-Host 'zapret (Flowseal)'
$zp = Latest 'Flowseal/zapret-discord-youtube'
Fetch "https://github.com/Flowseal/zapret-discord-youtube/releases/download/$($zp.tag_name)/zapret-discord-youtube-$($zp.tag_name).zip" "$tmp\zapret.zip"
Unzip-Into "$tmp\zapret.zip" "$bin\zapret\current"
if (-not (Test-Path "$bin\zapret\current\bin\winws.exe")) { throw 'winws.exe not found in the zapret release' }
Set-Content -Path "$bin\zapret\current\.version" -Value $zp.tag_name -NoNewline -Encoding ascii

Copy-Item (Join-Path $PSScriptRoot 'release-readme.txt') (Join-Path $app 'ПРОЧТИ.txt') -Force

$name = if ($Version) { "Detour-$Version-win-x64.zip" } else { 'Detour-win-x64.zip' }
$zip = Join-Path $root "$OutDir\$name"
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "Zipping $name"
Compress-Archive -Path $app -DestinationPath $zip
Remove-Item $tmp -Recurse -Force
Write-Host "Done: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Write-Host "Xray $($xr.tag_name), sing-box $($sb.tag_name), zapret $($zp.tag_name)"
