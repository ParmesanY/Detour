param([string]$Label = 'test')
# Live diagnostics while Detour is connected. Writes publish\Detour\guiLogs\diag-<label>-<time>.txt.
# No keys, passwords or server addresses are written: only processes, ports, proxy settings and timings.

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$logDir = Join-Path $root 'publish\Detour\guiLogs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$out = Join-Path $logDir ("diag-{0}-{1:yyyyMMdd-HHmmss}.txt" -f $Label, (Get-Date))
$lines = New-Object System.Collections.Generic.List[string]
function W([string]$s) { $lines.Add($s); Write-Host $s }

W "=== Detour live diagnostics [$Label] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

W "`n--- processes"
Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Detour|v2rayN|xray|sing-box|winws|Throne|ThroneCore|TgWsProxy.*|mihomo)\.exe$' } |
    ForEach-Object { W ("{0,-24} pid={1,-6} parent={2,-6} start={3} path={4}" -f $_.Name, $_.ProcessId, $_.ParentProcessId, $_.CreationDate.ToString('HH:mm:ss'), $_.ExecutablePath) }

W "`n--- adapters up / default routes"
Get-NetAdapter | Where-Object Status -eq Up | ForEach-Object { W ("{0} ({1}) idx={2} mtu={3}" -f $_.Name, $_.InterfaceDescription, $_.ifIndex, (Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).NlMtu) }
Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.DestinationPrefix -in '0.0.0.0/0', '0.0.0.0/1', '128.0.0.0/1' } |
    ForEach-Object { W ("route {0} via {1} metric={2}" -f $_.DestinationPrefix, $_.InterfaceAlias, ($_.RouteMetric + $_.InterfaceMetric)) }

W "`n--- listening ports"
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 10808, 10809, 10810, 10811, 10813, 10814, 1443, 2080 } |
    ForEach-Object { $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue; W ("{0}:{1} <- {2} (pid {3})" -f $_.LocalAddress, $_.LocalPort, $p.ProcessName, $_.OwningProcess) }

W "`n--- system proxy"
$k = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
W ("ProxyEnable={0} ProxyServer={1} AutoConfigURL={2}" -f $k.ProxyEnable, $k.ProxyServer, $k.AutoConfigURL)
W ("ProxyOverride={0}" -f $k.ProxyOverride)

W "`n--- DNS (system resolver)"
foreach ($h in 'www.youtube.com', 'www.google.com', 'en.wikipedia.org', 'ya.ru') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $ips = [Net.Dns]::GetHostAddresses($h) | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 3; W ("{0,-18} {1,5} ms  {2}" -f $h, $sw.ElapsedMilliseconds, ($ips -join ',')) }
    catch { W ("{0,-18} {1,5} ms  FAIL {2}" -f $h, $sw.ElapsedMilliseconds, $_.Exception.InnerException.Message) }
}

$bulk = 'https://speed.cloudflare.com/__down?bytes=5000000'
$sites = 'https://www.gstatic.com/generate_204', 'https://www.youtube.com/', 'https://www.google.com/search?q=detour', 'https://en.wikipedia.org/wiki/Finland', 'https://ya.ru/'
$fmt = 'code=%{http_code} size=%{size_download} connect=%{time_connect}s tls=%{time_appconnect}s total=%{time_total}s speed=%{speed_download}B/s'

function Probe([string]$name, [string[]]$extra) {
    W "`n--- path: $name"
    foreach ($u in @($sites + $bulk)) {
        $short = ($u -replace '^https://', '' -replace '\?.*$', '').Substring(0, [Math]::Min(40, ($u -replace '^https://', '' -replace '\?.*$', '').Length))
        $r = & curl.exe -s -o NUL -w $fmt --max-time 25 @extra $u 2>&1
        W ("{0,-40} {1}" -f $short, $r)
    }
}

Probe 'default route (TUN if on)' @()
Probe 'system proxy (as browsers use it)' @('--proxy', $(if ($k.ProxyEnable -eq 1 -and $k.ProxyServer) { "http://$($k.ProxyServer -replace '^.*=','')" } else { 'http://127.0.0.1:10808' }))
Probe 'socks5h 127.0.0.1:10808' @('-x', 'socks5h://127.0.0.1:10808')
Probe 'http 127.0.0.1:10809' @('-x', 'http://127.0.0.1:10809')

W "`n--- Detour settings (no secrets)"
$cfgPath = Join-Path $root 'publish\Detour\guiConfigs\guiNConfig.json'
if (Test-Path $cfgPath) {
    $c = Get-Content $cfgPath -Raw | ConvertFrom-Json
    W ("tun={0} stack={1} mtu={2} strict={3} sysproxy={4} zapret={5} strategy={6} killswitch={7} protectLocalProxy={8}" -f $c.TunModeItem.EnableTun, $c.TunModeItem.Stack, $c.TunModeItem.Mtu, $c.TunModeItem.StrictRoute, $c.SystemProxyItem.SysProxyType, $c.ZapretItem.Enabled, $c.ZapretItem.Strategy, $c.KillSwitchEnabled, $c.ProtectLocalProxy)
    W ("dns remote={0} direct={1} bootstrap={2} commonHosts={3} fakeip={4}" -f $c.SimpleDNSItem.RemoteDNS, $c.SimpleDNSItem.DirectDNS, $c.SimpleDNSItem.BootstrapDNS, $c.SimpleDNSItem.AddCommonHosts, $c.SimpleDNSItem.FakeIP)
    W ("inbound local port={0} sniffing={1} allowLan={2}" -f $c.Inbound[0].LocalPort, $c.Inbound[0].SniffingEnabled, $c.Inbound[0].AllowLANConn)
}
$bc = Join-Path $root 'publish\Detour\binConfigs'
Get-ChildItem $bc -File -ErrorAction SilentlyContinue | ForEach-Object { W ("{0} {1:HH:mm:ss} {2} bytes" -f $_.Name, $_.LastWriteTime, $_.Length) }
$pre = Join-Path $bc 'configPre.json'
if (Test-Path $pre) {
    $j = Get-Content $pre -Raw | ConvertFrom-Json
    $j.inbounds | ForEach-Object { W ("pre inbound {0} {1}:{2} {3}" -f $_.type, $_.listen, $_.listen_port, $_.interface_name) }
    $j.outbounds | ForEach-Object { W ("pre outbound {0} {1} port={2} auth={3}" -f $_.tag, $_.type, $_.server_port, [bool]$_.username) }
}

W "`n--- core log tail"
$core = Get-ChildItem $logDir -Filter 'core-*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
# Server names and addresses are masked: the report is meant to be shared.
if ($core) {
    Get-Content $core.FullName -Tail 60 -Encoding utf8 | ForEach-Object {
        $line = $_ -replace '\b(\d{1,3}\.){3}\d{1,3}\b', '<ip>'
        $line = $line -replace '\b([a-z0-9-]+\.)+(net|org|com|cc|ru|io|xyz|me|top|site|pro|dev|app|cloud|online|link|fi|de|nl|pl|lv)\b', '<host>'
        W $line
    }
}

$lines | Set-Content -Path $out -Encoding utf8
Write-Host "`nSaved: $out"
