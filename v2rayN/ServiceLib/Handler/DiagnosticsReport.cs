using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ServiceLib.Handler;

/// <summary>
/// "Diagnostics" button: one text report a user can paste into an issue. It tests the same paths
/// a browser uses (direct, the local proxy port) and lists what usually breaks things (another VPN
/// client, a foreign process on the core port, system proxy). Server addresses, IPs, keys and the
/// Windows user name are masked; nothing is sent anywhere.
/// </summary>
public static partial class DiagnosticsReport
{
    private static readonly string[] Processes =
        ["Detour", "xray", "sing-box", "winws", "TgWsProxy", "mihomo", "Throne", "ThroneCore", "nekoray", "Hiddify", "v2rayN"];

    private static readonly (string Name, string Url)[] Probes =
    [
        ("generate_204", "https://www.gstatic.com/generate_204"),
        ("youtube", "https://www.youtube.com/"),
        ("google", "https://www.google.com/"),
        ("wikipedia", "https://en.wikipedia.org/"),
        ("discord", "https://discord.com/"),
        ("ya.ru", "https://ya.ru/"),
    ];

    private const string SpeedUrl = "https://speed.cloudflare.com/__down?bytes=3000000";

    public static async Task<string> BuildAsync(Config config, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        void W(string line = "") => sb.AppendLine(line);

        W($"=== Detour diagnostics {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"app {AppUpdateManager.CurrentVersion} (base {Utils.GetVersionInfo()}), {Environment.OSVersion}, admin={Utils.IsAdministrator()}");

        W();
        W("--- processes");
        foreach (var name in Processes)
        {
            foreach (var p in SafeProcesses(name))
            {
                W($"{p.ProcessName,-14} pid={p.Id}");
            }
        }
        var conflicts = VpnConflictDetector.Detect();
        W(conflicts.Count == 0 ? "other VPN clients: none" : $"other VPN clients: {string.Join(", ", conflicts)}");

        W();
        W("--- settings");
        var tun = config.TunModeItem;
        W($"tun={tun.EnableTun} stack={tun.Stack} mtu={tun.Mtu} strictRoute={tun.StrictRoute} sysproxy={config.SystemProxyItem.SysProxyType}");
        W($"zapret={config.ZapretItem.Enabled} installed={ZapretManager.Instance.IsInstalled} running={ZapretManager.Instance.IsRunning} " +
          $"strategy={config.ZapretItem.Strategy} version={config.ZapretItem.InstalledVersion ?? ZapretManager.BundledVersion}");
        W($"tgwsproxy={config.TgWsProxyItem.Enabled} running={TgWsProxyManager.Instance.IsRunning} killswitch={config.KillSwitchEnabled} " +
          $"protectLocalProxy={config.ProtectLocalProxy}");
        W($"dns remote={Mask(config.SimpleDNSItem.RemoteDNS)} direct={Mask(config.SimpleDNSItem.DirectDNS)} fakeip={config.SimpleDNSItem.FakeIP}");
        var profile = config.IndexId.IsNullOrEmpty() ? null : await AppManager.Instance.GetProfileItem(config.IndexId);
        W(profile == null
            ? "server: none selected"
            : $"server: type={profile.ConfigType} core={profile.CoreType?.ToString() ?? "default"} network={profile.GetNetwork()}");

        W();
        W("--- local ports");
        var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        foreach (var port in new[] { socksPort, socksPort + 1, socksPort + 2, socksPort + 3 }.Distinct())
        {
            var pid = PortOwner.ListenerPid(port);
            W(pid is null ? $"{port}: free" : $"{port}: {ProcessName(pid.Value)} (pid {pid})");
        }

        W();
        W("--- network");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var gw = nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                     && !g.Address.Equals(IPAddress.Any));
            W($"{nic.Name} ({nic.Description}) type={nic.NetworkInterfaceType} gateway={gw}");
        }
        W($"system proxy: {SystemProxyState()}");

        W();
        W("--- DNS (system resolver)");
        foreach (var host in new[] { "www.youtube.com", "www.google.com", "discord.com", "ya.ru" })
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var ips = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
                W($"{host,-16} {sw.ElapsedMilliseconds,5} ms  {ips.Length} addr, {DescribeAddress(ips.FirstOrDefault())}");
            }
            catch (Exception ex)
            {
                W($"{host,-16} {sw.ElapsedMilliseconds,5} ms  FAIL {ex.GetBaseException().Message}");
            }
        }

        await ProbePath(W, "direct / TUN (default route)", null, cancellationToken);
        await ProbePath(W, $"local proxy socks5 127.0.0.1:{socksPort}", new WebProxy($"socks5://127.0.0.1:{socksPort}"), cancellationToken);

        W();
        W("--- core log (last 40 lines, masked)");
        try
        {
            var file = CoreLog.FilePath;
            if (File.Exists(file))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var lines = (await reader.ReadToEndAsync(cancellationToken)).Split('\n');
                foreach (var line in lines.Where(l => l.Trim().Length > 0).TakeLast(40))
                {
                    W(Mask(line.TrimEnd('\r')));
                }
            }
            else
            {
                W("(no core log today)");
            }
        }
        catch (Exception ex)
        {
            W($"(log unreadable: {ex.Message})");
        }

        return sb.ToString();
    }

    private static async Task ProbePath(Action<string> w, string title, IWebProxy? proxy, CancellationToken cancellationToken)
    {
        w("");
        w($"--- path: {title}");
        using var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Detour-diagnostics");
        foreach (var (name, url) in Probes)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                w($"{name,-14} {(int)resp.StatusCode,3}  {sw.ElapsedMilliseconds,5} ms");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                w($"{name,-14} ERR  {sw.ElapsedMilliseconds,5} ms  {Mask(ex.GetBaseException().Message)}");
            }
        }
        var speedWatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await client.GetByteArrayAsync(SpeedUrl, cts.Token);
            var mbit = bytes.Length * 8 / 1_000_000.0 / Math.Max(0.001, speedWatch.Elapsed.TotalSeconds);
            w($"{"speed 3 MB",-14} {mbit,6:F1} Mbit/s");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            w($"{"speed 3 MB",-14} ERR  {Mask(ex.GetBaseException().Message)}");
        }
    }

    private static IEnumerable<Process> SafeProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return [];
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    private static string SystemProxyState()
    {
        if (!Utils.IsWindows())
        {
            return "n/a";
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable") is int i && i == 1;
            var server = key?.GetValue("ProxyServer") as string;
            var pac = key?.GetValue("AutoConfigURL") as string;
            return $"enabled={enabled} server={Mask(server)} pac={(pac.IsNullOrEmpty() ? "-" : "set")}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Fake-IP (198.18/15), loopback and private ranges are worth seeing; anything else is masked.</summary>
    private static string DescribeAddress(IPAddress? ip)
    {
        if (ip == null)
        {
            return "-";
        }
        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            127 => "loopback",
            10 => "private 10.x",
            192 when b[1] == 168 => "private 192.168.x",
            172 when b[1] is >= 16 and <= 31 => "private 172.x",
            198 when b[1] is 18 or 19 => "fake-ip 198.18.x",
            0 => "0.0.0.0 (blocked)",
            _ => "public",
        };
    }

    /// <summary>Masks IPs (except loopback), host names, URLs' credentials and the Windows user name.</summary>
    public static string Mask(string? text)
    {
        if (text.IsNullOrEmpty())
        {
            return text ?? "";
        }
        var s = UserInfoRegex().Replace(text, "://<user>@");
        s = Ipv4Regex().Replace(s, m => m.Value.StartsWith("127.") ? m.Value : "<ip>");
        s = HostRegex().Replace(s, "<host>");
        var user = Environment.UserName;
        if (user.Length > 2)
        {
            s = s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UserInfoRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+(?:net|org|com|cc|ru|io|xyz|me|top|site|pro|dev|app|cloud|online|link|fi|de|nl|pl|lv|uk|us|info|biz|su|kz|ee|lt|se|ch|at|fr|tr|jp|sg|hk|vip|live|space|tech|store|club|shop|icu|one|world|host|network)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HostRegex();
}
