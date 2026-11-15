using System.Net.Security;
using System.Security.Authentication;

namespace ServiceLib.Handler;

/// <summary>
/// "Check the network": what the ISP blocks and how, in a text report with advice. Every connection is bound
/// to the physical network card and names are resolved over DoH, so the VPN tunnel doesn't hide the ISP.
/// zapret, if running, still acts on its listed sites: the report says so.
/// </summary>
public static class NetCheckReport
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(7);
    private const int _maxBytes = 64 * 1024;

    public sealed record HttpsResult(bool Tcp, bool Tls, int Bytes, long Ms, string Status, string Error, bool DpiLike);

    private static readonly (string Name, string Host, string Path)[] _sites =
    [
        ("Яндекс (контроль)", "ya.ru", "/"),
        ("Cloudflare", "speed.cloudflare.com", "/__down?bytes=65536"),
        ("Hetzner", "nbg1-speed.hetzner.com", "/100MB.bin"),
        ("OVH", "proof.ovh.net", "/files/1Mb.dat"),
        ("Vultr", "fra-de-ping.vultr.com", "/vultr.com.100MB.bin"),
        ("Linode/Akamai", "speedtest.frankfurt.linode.com", "/100MB-frankfurt.bin"),
        ("Google", "www.google.com", "/"),
        ("YouTube", "www.youtube.com", "/"),
        ("Discord", "discord.com", "/"),
        ("Telegram", "telegram.org", "/"),
        ("Instagram", "www.instagram.com", "/"),
        ("X (Twitter)", "x.com", "/"),
        ("Rutracker", "rutracker.org", "/forum/index.php"),
        ("ChatGPT", "chatgpt.com", "/"),
        ("GitHub", "github.com", "/"),
    ];

    private static readonly string[] _hosting = ["Cloudflare", "Hetzner", "OVH", "Vultr", "Linode/Akamai"];

    public static async Task<string> BuildAsync(Config config, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var local = ZapretWatchdog.PhysicalIPv4Address();
        sb.AppendLine($"=== {AppText.NetCheckTitle} {DateTime.Now:yyyy-MM-dd HH:mm} ===");
        sb.AppendLine(local == null ? AppText.NetCheckNoNic : AppText.NetCheckViaNic);
        if (ZapretManager.Instance.IsRunning)
        {
            sb.AppendLine(AppText.NetCheckZapretOn);
        }
        sb.AppendLine();

        // 1. Sites and hosting: ISP DNS vs DoH, then a 64 KB download over the real address.
        sb.AppendLine(AppText.NetCheckSites);
        var dnsStubs = 0;
        var frozenHosting = new List<string>();
        var blockedSites = new List<string>();
        var realIps = new Dictionary<string, IPAddress>();
        foreach (var (name, host, path) in _sites)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(name);
            var system = await ResolveSystemAsync(host, ct);
            var doh = await ZapretWatchdog.ResolveDirectAsync(host, local, ct);
            string dns;
            if (system.Length == 0)
            {
                dns = AppText.NetCheckDnsNone;
            }
            else if (system.Any(IsBogon))
            {
                dns = string.Format(AppText.NetCheckDnsStub, system[0]);
                dnsStubs++;
            }
            else
            {
                dns = AppText.NetCheckDnsOk;
            }
            var ip = doh.FirstOrDefault() ?? system.FirstOrDefault(a => !IsBogon(a));
            string line;
            if (ip == null)
            {
                line = AppText.NetCheckNoIp;
            }
            else
            {
                realIps[host] = ip;
                var r = await HttpsAsync(ip, host, host, path, local, ct);
                line = Format(r);
                if (_hosting.Contains(name) && r.Tls && r.Error.Length > 0 && r.Bytes is > 0 and < 40_000)
                {
                    frozenHosting.Add(name);
                }
                if (!_hosting.Contains(name) && (r.DpiLike || !r.Tcp))
                {
                    blockedSites.Add(name);
                }
            }
            sb.AppendLine($"{name,-20} {dns}; {line}");
        }
        sb.AppendLine();

        // 2. SNI filter: one foreign server, a blocked name vs its own name. A server refusing an unknown
        // name answers with a TLS alert; DPI resets the connection or goes silent.
        sb.AppendLine(AppText.NetCheckSni);
        var sniDpi = false;
        if (realIps.TryGetValue("speed.cloudflare.com", out var cfIp))
        {
            foreach (var (label, sni) in new[] { (AppText.NetCheckSniOwn, "speed.cloudflare.com"), ("rutracker.org", "rutracker.org"), ("discord.com", "discord.com") })
            {
                progress?.Report("SNI " + sni);
                var r = await HttpsAsync(cfIp, sni, "speed.cloudflare.com", "/__down?bytes=65536", local, ct);
                sniDpi |= r.DpiLike && sni != "speed.cloudflare.com";
                sb.AppendLine($"Cloudflare, SNI {label,-22} {Format(r)}");
            }
        }
        sb.AppendLine();

        // 3. Plain TCP to addresses that are often blocked by IP.
        sb.AppendLine(AppText.NetCheckIps);
        var ipBlocked = new List<string>();
        foreach (var (name, addr, port) in new[]
                 {
                     ("Telegram DC2", "149.154.167.51", 443), ("Telegram DC4", "149.154.167.91", 443),
                     ("Cloudflare WARP", "162.159.192.1", 2408), ("Cloudflare 1.1.1.1", "1.1.1.1", 443),
                 })
        {
            progress?.Report(name);
            var ok = await TcpAsync(IPAddress.Parse(addr), port, local, ct);
            if (!ok)
            {
                ipBlocked.Add(name);
            }
            sb.AppendLine($"{name,-20} {addr}:{port}  {(ok ? AppText.NetCheckOk : AppText.NetCheckNoAnswer)}");
        }
        sb.AppendLine();

        // Advice from what was found.
        sb.AppendLine(AppText.NetCheckAdvice);
        var advice = new List<string>();
        if (dnsStubs > 0)
        {
            advice.Add(AppText.NetCheckAdviceDns);
        }
        if (frozenHosting.Count > 0)
        {
            advice.Add(string.Format(AppText.NetCheckAdviceFreeze, string.Join(", ", frozenHosting)));
        }
        if (sniDpi || blockedSites.Count > 0)
        {
            advice.Add(string.Format(AppText.NetCheckAdviceSni, string.Join(", ", blockedSites.DefaultIfEmpty("—"))));
        }
        if (ipBlocked.Count > 0)
        {
            advice.Add(string.Format(AppText.NetCheckAdviceIp, string.Join(", ", ipBlocked)));
        }
        if (advice.Count == 0)
        {
            advice.Add(AppText.NetCheckAdviceNone);
        }
        foreach (var a in advice)
        {
            sb.AppendLine("• " + a);
        }
        return sb.ToString();
    }

    public static string Format(HttpsResult r)
    {
        if (!r.Tcp)
        {
            return string.Format(AppText.NetCheckIpDead, r.Error);
        }
        if (!r.Tls)
        {
            return string.Format(AppText.NetCheckTlsFail, r.Error);
        }
        var kb = Math.Round(r.Bytes / 1024.0, 1);
        if (r.Error.Length > 0)
        {
            return r.Bytes is > 0 and < 40_000
                ? string.Format(AppText.NetCheckFreeze, kb, r.Ms)
                : string.Format(AppText.NetCheckCut, kb, r.Error);
        }
        return r.Bytes == 0 ? AppText.NetCheckNoReply : string.Format(AppText.NetCheckWorks, kb, r.Ms, r.Status);
    }

    /// <summary>Private, loopback, CGNAT and benchmark ranges: what ISP DNS stubs return.</summary>
    public static bool IsBogon(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = a.GetAddressBytes();
        return b[0] is 0 or 10 or 127
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && b[1] is >= 64 and <= 127)
               || (b[0] == 198 && b[1] is 18 or 19);
    }

    private static async Task<IPAddress[]> ResolveSystemAsync(string host, CancellationToken ct)
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static async Task<bool> TcpAsync(IPAddress ip, int port, IPAddress? local, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var s = await ZapretWatchdog.ConnectBoundAsync([ip], port, local, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>TCP, TLS with the given SNI, GET with the given Host, read up to 64 KB.</summary>
    public static async Task<HttpsResult> HttpsAsync(IPAddress ip, string sni, string host, string path, IPAddress? local, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        Stream stream;
        try
        {
            stream = await ZapretWatchdog.ConnectBoundAsync([ip], 443, local, cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(false, false, 0, sw.ElapsedMilliseconds, "", ex is OperationCanceledException ? AppText.NetCheckTimeout : ex.Message, false);
        }

        await using var _ = stream;
        await using var ssl = new SslStream(stream, false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, cts.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var (text, dpi) = ClassifyTlsError(ex);
            return new(true, false, 0, sw.ElapsedMilliseconds, "", text, dpi);
        }

        var bytes = 0;
        var status = "";
        try
        {
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/142.0\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(request, cts.Token);
            var buffer = new byte[16384];
            while (bytes < _maxBytes)
            {
                var n = await ssl.ReadAsync(buffer, cts.Token);
                if (n == 0)
                {
                    break;
                }
                if (bytes == 0)
                {
                    status = Encoding.ASCII.GetString(buffer, 0, Math.Min(n, 15)).Trim();
                }
                bytes += n;
            }
            return new(true, true, bytes, sw.ElapsedMilliseconds, status, "", false);
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(true, true, bytes, sw.ElapsedMilliseconds, status, AppText.NetCheckStalled, bytes is > 0 and < 40_000);
        }
    }

    /// <summary>A TLS alert means the server itself refused; a reset or silence is what DPI does.</summary>
    private static (string Text, bool DpiLike) ClassifyTlsError(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return (AppText.NetCheckTlsSilence, true);
        }
        var inner = ex;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }
        return inner is SocketException { SocketErrorCode: SocketError.ConnectionReset }
            ? (AppText.NetCheckTlsReset, true)
            : ex is AuthenticationException
                ? (AppText.NetCheckTlsAlert, false)
                : (inner.Message, false);
    }
}
