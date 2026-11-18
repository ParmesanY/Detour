param(
    [Parameter(Mandatory)][string]$Tag,       # e.g. v0.3.9
    [string]$Repo = 'ParmesanY/Detour',
    [string]$KeyFile = ''
)
# Signs a published release on the maintainer's machine: downloads the archives CI attached, checks them
# against the release's SHA256SUMS.txt, signs them with the offline key (detour-sign.cs asks for its password)
# and uploads only the .sig files. Needs gh (logged in) and the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) "detour-sign-$Tag"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory $dir | Out-Null

gh release download $Tag --repo $Repo --dir $dir --pattern 'Detour-*' --pattern 'SHA256SUMS.txt'
if ($LASTEXITCODE) { throw 'gh release download failed' }

$archives = Get-ChildItem $dir -File | Where-Object { $_.Name -match '^Detour-.*\.(zip|tar\.gz)$' }
if (-not $archives) { throw "no archives in release $Tag" }
$sums = @{}
foreach ($line in Get-Content (Join-Path $dir 'SHA256SUMS.txt')) {
    $p = $line -split '\s+', 2
    if ($p.Count -eq 2) { $sums[$p[1].TrimStart('*')] = $p[0] }
}
foreach ($a in $archives) {
    $h = (Get-FileHash $a.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sums[$a.Name] -ne $h) { throw "$($a.Name) doesn't match SHA256SUMS.txt" }
}

$signArgs = @('run', '--file', (Join-Path $PSScriptRoot 'detour-sign.cs'), '--', 'sign') + ($archives | ForEach-Object FullName)
if ($KeyFile) { $signArgs += @('--key', $KeyFile) }
dotnet @signArgs
if ($LASTEXITCODE) { throw 'signing failed' }

$sigs = $archives | ForEach-Object { "$($_.FullName).sig" }
gh release upload $Tag --repo $Repo --clobber @sigs
if ($LASTEXITCODE) { throw 'gh release upload failed' }
Write-Host "Signed and uploaded $($sigs.Count) signatures for $Tag"
Remove-Item $dir -Recurse -Force
