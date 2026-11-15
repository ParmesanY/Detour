<# : batch part (PowerShell sees this as a comment)
@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText('%~f0',[Text.Encoding]::UTF8); iex $s"
pause
exit /b
#>

# Detour network check: what the ISP blocks and how. Changes nothing on the computer, needs no admin rights.
# Checks: DNS substitution, IP blocks, the "16 KB" freeze on popular hosting, SNI filtering, whitelist mode, UDP.
# The report goes to the Desktop; it contains no IP address, only the provider name and city.

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$TimeoutMs = 7000
$MaxBytes = 65536
$report = New-Object System.Collections.Generic.List[string]
function Out-Line([string]$text) { Write-Host $text; $report.Add($text) }

Out-Line "=== Detour: проверка интернета $(Get-Date -Format 'yyyy-MM-dd HH:mm') ==="
Write-Host "Займёт 3-5 минут. Ничего не меняет на компьютере."
Write-Host ""
Write-Host "Какое подключение? 1 - домашний интернет (Wi-Fi/кабель), 2 - мобильный интернет / раздача с телефона"
$kind = Read-Host "Введите 1 или 2"
Out-Line ("Подключение: " + $(if ($kind -eq '2') { 'мобильное' } else { 'домашнее' }))

# --- Is a VPN or zapret running? Their traffic would spoil the results. ---
$vpnNics = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
    $_.Status -eq 'Up' -and ($_.InterfaceDescription -match 'TAP|TUN|Wintun|WireGuard|OpenVPN|sing-box|Xray|Amnezia|Outline' -or $_.Name -match 'tun|wg|vpn')
}
$dpiProcs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(winws|goodbyedpi|byedpi|ciadpi|zapret)$' }
if ($vpnNics) { Out-Line ("ВНИМАНИЕ: включён VPN-адаптер: " + (($vpnNics | ForEach-Object { $_.InterfaceDescription }) -join ', ') + ". Выключите VPN и запустите заново.") }
if ($dpiProcs) { Out-Line ("ВНИМАНИЕ: запущен обход DPI: " + (($dpiProcs | ForEach-Object { $_.ProcessName }) -join ', ') + ". Выключите его и запустите заново.") }

# --- Provider (no IP in the report) ---
$provider = $null
try { $i = Invoke-RestMethod -Uri 'https://ipinfo.io/json' -TimeoutSec 10; $provider = "$($i.org); город: $($i.city), $($i.region), $($i.country)" } catch { }
if (-not $provider) {
    try { $i = Invoke-RestMethod -Uri 'https://ipwho.is/' -TimeoutSec 10; $provider = "$($i.connection.org) ($($i.connection.isp)); город: $($i.city), $($i.region), $($i.country)" } catch { }
}
Out-Line ("Провайдер: " + $(if ($provider) { $provider } else { 'не удалось узнать' }))
Out-Line ""

# --- Helpers ---
function Resolve-Doh([string]$name) {
    foreach ($url in @("https://1.1.1.1/dns-query?name=$name&type=A", "https://8.8.8.8/resolve?name=$name&type=A")) {
        try {
            $r = Invoke-RestMethod -Uri $url -Headers @{ accept = 'application/dns-json' } -TimeoutSec 8
            $ips = @($r.Answer | Where-Object { $_.type -eq 1 } | ForEach-Object { $_.data })
            if ($ips.Count -gt 0) { return $ips }
        } catch { }
    }
    return @()
}

# Addresses a real site never has: private, loopback, carrier-grade NAT, test ranges (ISP stubs).
function Test-Bogon([string]$a) {
    return $a -match '^(0\.|10\.|127\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.|100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.|198\.1[89]\.)'
}

function Resolve-System([string]$name) {
    try { return @([Net.Dns]::GetHostAddresses($name) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.ToString() }) }
    catch { return @() }
}

# TCP connect, TLS handshake with the given SNI, GET with the given Host, read up to $max bytes.
function Test-Https([string]$ip, [string]$sni, [string]$hostHeader, [string]$path, [int]$max = $MaxBytes) {
    $res = [ordered]@{ Tcp = $false; Tls = $false; Bytes = 0; Ms = 0; Status = ''; Error = '' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $tcp = New-Object Net.Sockets.TcpClient
    try {
        $task = $tcp.ConnectAsync($ip, 443)
        try { $done = $task.Wait($TimeoutMs) }
        catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; $res.Error = 'TCP: ' + $e.Message; return $res }
        if (-not $done) { $res.Error = 'TCP: нет ответа'; return $res }
        $res.Tcp = $true
        $tcp.ReceiveTimeout = $TimeoutMs; $tcp.SendTimeout = $TimeoutMs
        $cb = [Net.Security.RemoteCertificateValidationCallback] { $true }
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, $cb)
        $ssl.ReadTimeout = $TimeoutMs; $ssl.WriteTimeout = $TimeoutMs
        try { $ssl.AuthenticateAsClient($sni, $null, [Security.Authentication.SslProtocols]::Tls12, $false) }
        catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; $res.Error = 'TLS: ' + $e.Message; return $res }
        $res.Tls = $true
        $req = "GET $path HTTP/1.1`r`nHost: $hostHeader`r`nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/142.0`r`nAccept: */*`r`nConnection: close`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($req)
        $ssl.Write($bytes, 0, $bytes.Length)
        $buf = New-Object byte[] 16384
        while ($res.Bytes -lt $max) {
            try { $n = $ssl.Read($buf, 0, $buf.Length) } catch { $res.Error = 'обрыв/зависание при чтении'; break }
            if ($n -le 0) { break }
            if ($res.Bytes -eq 0) { $res.Status = ([Text.Encoding]::ASCII.GetString($buf, 0, [Math]::Min($n, 15))).Trim() }
            $res.Bytes += $n
        }
    } catch {
        $res.Error = $_.Exception.Message
    } finally {
        $res.Ms = $sw.ElapsedMilliseconds
        $tcp.Close()
    }
    return $res
}

function Format-Result($r) {
    if (-not $r.Tcp) { return "IP НЕ ОТВЕЧАЕТ ($($r.Error))" }
    if (-not $r.Tls) { return "TCP есть, TLS ОБОРВАН ($($r.Error))" }
    $kb = [Math]::Round($r.Bytes / 1024, 1)
    if ($r.Error) {
        if ($r.Bytes -gt 0 -and $r.Bytes -lt 40000) { return "ЗАВИС после $kb КБ ($($r.Ms) мс) - похоже на блокировку 16 КБ" }
        return "оборвалось на $kb КБ: $($r.Error)"
    }
    if ($r.Bytes -eq 0) { return "TLS есть, но ответа нет: соединение закрыли ($($r.Ms) мс)" }
    return "ок, $kb КБ за $($r.Ms) мс [$($r.Status)]"
}

# --- 1. Sites and hosting: DNS (system vs DoH), then a 64 KB download over the real IP ---
$targets = @(
    @('Яндекс (контроль)', 'ya.ru', '/'),
    @('VK (контроль)', 'vk.com', '/'),
    @('Cloudflare', 'speed.cloudflare.com', '/__down?bytes=65536'),
    @('Hetzner', 'nbg1-speed.hetzner.com', '/100MB.bin'),
    @('OVH', 'proof.ovh.net', '/files/1Mb.dat'),
    @('Linode/Akamai', 'speedtest.frankfurt.linode.com', '/100MB-frankfurt.bin'),
    @('Vultr', 'fra-de-ping.vultr.com', '/vultr.com.100MB.bin'),
    @('Google', 'www.google.com', '/'),
    @('YouTube', 'www.youtube.com', '/'),
    @('YouTube видео (googlevideo)', 'redirector.googlevideo.com', '/generate_204'),
    @('Discord', 'discord.com', '/'),
    @('Telegram (сайт)', 'telegram.org', '/'),
    @('Instagram', 'www.instagram.com', '/'),
    @('X (Twitter)', 'x.com', '/'),
    @('Rutracker', 'rutracker.org', '/forum/index.php'),
    @('ChatGPT', 'chatgpt.com', '/'),
    @('GitHub', 'github.com', '/'),
    @('LinkedIn', 'www.linkedin.com', '/'),
    @('Steam', 'store.steampowered.com', '/'),
    @('Twitch', 'www.twitch.tv', '/')
)
$resolved = @{}
Out-Line "=== 1. Сайты и хостинги (DNS провайдера / настоящий IP / загрузка 64 КБ) ==="
$i = 0
foreach ($t in $targets) {
    $i++
    Write-Progress -Activity 'Сайты и хостинги' -Status $t[0] -PercentComplete ($i * 100 / $targets.Count)
    $sys = @(Resolve-System $t[1])
    $doh = @(Resolve-Doh $t[1])
    $resolved[$t[1]] = $doh
    $dns = if ($sys.Count -eq 0) { 'DNS провайдера: нет ответа' }
           elseif ($sys | Where-Object { Test-Bogon $_ }) { "DNS провайдера ПОДМЕНЯЕТ (заглушка $($sys[0]))" }
           elseif ($doh.Count -gt 0 -and -not ($sys | Where-Object { $doh -contains $_ })) { "DNS: IP не как у DoH ($($sys[0]) / $($doh[0]))" }
           else { 'DNS ок' }
    $ip = if ($doh.Count -gt 0) { $doh[0] } elseif ($sys.Count -gt 0) { $sys[0] } else { $null }
    $res = if ($ip) { Format-Result (Test-Https $ip $t[1] $t[1] $t[2]) } else { 'не удалось узнать IP' }
    Out-Line ("{0,-28} {1}; {2}" -f $t[0], $dns, $res)
}
Out-Line ""

# --- 2. SNI filtering and whitelist mode: the same foreign server, different names in the TLS handshake ---
Out-Line "=== 2. Фильтр по имени сайта (SNI): один и тот же сервер, разные имена ==="
$sniServers = @(@('Hetzner', 'nbg1-speed.hetzner.com', '/100MB.bin'), @('Cloudflare', 'speed.cloudflare.com', '/__down?bytes=65536'))
$sniNames = @(
    @('своё имя', ''),
    @('заблокированное (rutracker.org)', 'rutracker.org'),
    @('белый список (vk.com)', 'vk.com'),
    @('белый список (gosuslugi.ru)', 'gosuslugi.ru'),
    @('случайное', "test-$(Get-Random -Maximum 99999).example.com"),
    @('без SNI', '-')
)
foreach ($s in $sniServers) {
    $ip = @($resolved[$s[1]])[0]
    if (-not $ip) { Out-Line "$($s[0]): нет IP, пропуск"; continue }
    foreach ($n in $sniNames) {
        $sni = if ($n[1] -eq '') { $s[1] } elseif ($n[1] -eq '-') { $ip } else { $n[1] }
        Write-Progress -Activity 'SNI' -Status "$($s[0]): $($n[0])"
        Out-Line ("{0,-11} SNI {1,-34} {2}" -f $s[0], $n[0], (Format-Result (Test-Https $ip $sni $s[1] $s[2])))
    }
}
Out-Line ""

# --- 3. IP blocks: plain TCP to known addresses ---
Out-Line "=== 3. Доступность IP (простое TCP-подключение) ==="
$ipTargets = @(
    @('Cloudflare DNS 1.1.1.1:443', '1.1.1.1', 443),
    @('Google DNS 8.8.8.8:443', '8.8.8.8', 443),
    @('Telegram DC2 149.154.167.51:443', '149.154.167.51', 443),
    @('Telegram DC4 149.154.167.91:443', '149.154.167.91', 443),
    @('Cloudflare WARP 162.159.192.1:2408', '162.159.192.1', 2408)
)
foreach ($t in $ipTargets) {
    $tcp = New-Object Net.Sockets.TcpClient
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $ok = $tcp.ConnectAsync($t[1], $t[2]).Wait(5000) } catch { $ok = $false }
    $state = if ($ok -and $tcp.Connected) { "ок, $($sw.ElapsedMilliseconds) мс" } else { 'НЕТ ОТВЕТА' }
    $tcp.Close()
    Out-Line ("{0,-38} {1}" -f $t[0], $state)
}
Out-Line ""

# --- 4. UDP: plain DNS queries (also shows whether the ISP intercepts port 53) ---
Out-Line "=== 4. UDP (DNS-запрос google.com) ==="
function Test-UdpDns([string]$server) {
    $udp = New-Object Net.Sockets.UdpClient
    try {
        $udp.Client.ReceiveTimeout = 4000
        $q = [byte[]](0x12,0x34,0x01,0x00,0x00,0x01,0x00,0x00,0x00,0x00,0x00,0x00,6) + [Text.Encoding]::ASCII.GetBytes('google') + [byte[]](3) + [Text.Encoding]::ASCII.GetBytes('com') + [byte[]](0,0,1,0,1)
        [void]$udp.Send($q, $q.Length, $server, 53)
        $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $a = $udp.Receive([ref]$ep)
        return "ок, ответ $($a.Length) байт от $($ep.Address)"
    } catch { return 'НЕТ ОТВЕТА' } finally { $udp.Close() }
}
foreach ($srv in @('8.8.8.8', '1.1.1.1', '77.88.8.8')) { Out-Line ("UDP DNS {0,-12} {1}" -f $srv, (Test-UdpDns $srv)) }
Out-Line ""

# --- Save ---
$file = Join-Path ([Environment]::GetFolderPath('Desktop')) ("detour-netcheck-" + (Get-Date -Format 'yyyyMMdd-HHmm') + ".txt")
$report | Out-File -FilePath $file -Encoding UTF8
Write-Host ""
Write-Host "Готово. Отчёт сохранён на рабочий стол: $file" -ForegroundColor Green
Write-Host "Отправьте этот файл. В нём нет вашего IP-адреса, только провайдер и город."
