param([string]$KeyFile = '')
# Builds strategies/trusted-assets.json from tools/cores.lock.json (URL -> SHA-256 of every pinned asset) and
# signs it with the release key (detour-sign.cs asks for the password). Released Detour installs Xray, sing-box,
# zapret and TG WS Proxy in the app only when the download is on this signed list. Run after
# update-cores-lock.ps1, then commit both files: the app reads them from the repo's main branch.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $PSScriptRoot 'cores.lock.json') -Raw | ConvertFrom-Json

$entries = New-Object System.Collections.Generic.List[string]
foreach ($key in $lock.PSObject.Properties.Name) {
    $p = $lock.$key
    foreach ($name in $p.sha256.PSObject.Properties.Name) {
        $hash = $p.sha256.$name
        if ($hash -notmatch '^[0-9a-f]{64}$') { throw "bad hash for $key/$name" }
        $entries.Add("    `"https://github.com/$($p.repo)/releases/download/$($p.tag)/$name`": `"$hash`"")
    }
}
# Plain ASCII with LF: the signature covers these exact bytes (.gitattributes keeps git from touching them).
$json = "{`n  `"version`": 1,`n  `"created`": `"$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`",`n  `"assets`": {`n" + ($entries -join ",`n") + "`n  }`n}`n"
$path = Join-Path $root 'strategies\trusted-assets.json'
[IO.File]::WriteAllText($path, $json, (New-Object Text.ASCIIEncoding))
Write-Host "$($entries.Count) assets -> $path"

$signArgs = @('run', '--file', (Join-Path $PSScriptRoot 'detour-sign.cs'), '--', 'sign', $path)
if ($KeyFile) { $signArgs += @('--key', $KeyFile) }
dotnet @signArgs
if ($LASTEXITCODE) { throw 'signing failed' }
Write-Host "Signed. Commit strategies/trusted-assets.json and strategies/trusted-assets.json.sig."
