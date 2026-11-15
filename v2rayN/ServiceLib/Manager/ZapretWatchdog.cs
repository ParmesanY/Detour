using System.Net.NetworkInformation;

namespace ServiceLib.Manager;

/// <summary>
/// Makes "zapret then VPN" rules safe: such a rule goes through the VPN until zapret has been
/// shown to work for every key host of the service over the direct path, and goes back to the
/// VPN as soon as zapret stops working. Checks bypass the tunnel (they are bound to the physical
/// network card), so they test the real direct path whatever the rule currently routes through.
/// </summary>
public class ZapretWatchdog
{
    private static readonly Lazy<ZapretWatchdog> _instance = new(() => new(ProbeAsync, () => DateTime.Now));
    public static ZapretWatchdog Instance => _instance.Value;

    private const string _tag = "ZapretWatchdog";

    /// <summary>Failed checks in a row before a rule leaves zapret for the VPN.</summary>
    public const int FailuresToFallback = 2;

    /// <summary>Successful checks in a row before a rule moves from the VPN to zapret.</summary>
    public const int SuccessesToUseZapret = 2;

    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    // A connection frozen by DPI typically stalls after ~16 KB, so a check must get past that.
    private const int ProbeBytes = 24 * 1024;
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(12);

    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, int> _successes = [];
    private int _started;

    public ZapretWatchdog(Func<string, CancellationToken, Task<bool>> probe, Func<DateTime> now)
    {
        _probe = probe;
        _now = now;
    }

    /// <summary>
    /// On start every "zapret then VPN" rule goes through the VPN: nothing is known yet about
    /// zapret on this network, and the user should not lose the site while it is being checked.
    /// </summary>
    public static void ResetToVpn(IEnumerable<ConnectionRule>? rules)
    {
        foreach (var rule in (rules ?? []).Where(r => r.Mode == EConnectionMode.ZapretThenVpn))
        {
            rule.FallbackActive = true;
        }
    }

    public void Start(Config config)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        Task.Run(async () =>
        {
            // First check soon after start, then every CheckInterval.
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    // Without zapret or with a global override there is nothing to decide.
                    if (ZapretManager.Instance.IsRunning && config.ConnectionRulesOverride == null)
                    {
                        var messages = await CheckOnceAsync(config.ConnectionRules ?? []);
                        if (messages.Count > 0)
                        {
                            await ConfigHandler.SaveConfig(config);
                            foreach (var m in messages)
                            {
                                NoticeManager.Instance.Enqueue(m);
                                Logging.SaveLog($"{_tag}: {m}");
                            }
                            AppEvents.CoreReloadRequested.Publish();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
                await Task.Delay(CheckInterval).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// One check round. Changes rule state in place and returns a message per switched rule;
    /// an empty list means routing does not need to be regenerated.
    /// </summary>
    public async Task<List<string>> CheckOnceAsync(IList<ConnectionRule> rules, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        foreach (var rule in rules.Where(r => r.Enabled && r.Mode == EConnectionMode.ZapretThenVpn))
        {
            var urls = ProbeUrls(rule);
            if (urls.Count == 0)
            {
                continue;
            }

            // Every key host must work: a service is broken if e.g. its gateway or video host is blocked.
            var results = await Task.WhenAll(urls.Select(u => _probe(u, cancellationToken)));
            var works = results.All(ok => ok);
            var name = DisplayName(rule);

            if (rule.FallbackActive)
            {
                // On VPN: move to zapret only after it worked several times in a row.
                var successes = works ? _successes.GetValueOrDefault(rule.Id) + 1 : 0;
                _successes[rule.Id] = successes;
                if (successes >= SuccessesToUseZapret)
                {
                    rule.FallbackActive = false;
                    rule.FallbackSince = null;
                    _successes[rule.Id] = 0;
                    _failures[rule.Id] = 0;
                    messages.Add(string.Format(AppText.WatchdogToZapret, name));
                }
                continue;
            }

            // On zapret: back to VPN after a few failures in a row.
            var failures = works ? 0 : _failures.GetValueOrDefault(rule.Id) + 1;
            _failures[rule.Id] = failures;
            if (failures >= FailuresToFallback)
            {
                rule.FallbackActive = true;
                rule.FallbackSince = _now();
                rule.FallbackCount++;
                _failures[rule.Id] = 0;
                _successes[rule.Id] = 0;
                messages.Add(string.Format(AppText.WatchdogToVpn, name));
            }
        }
        return messages;
    }

    /// <summary>
    /// URLs that must all work for a rule: the preset's own check list (site, gateway, CDN)
    /// or the first plain domain of the target.
    /// </summary>
    public static List<string> ProbeUrls(ConnectionRule rule)
    {
        var preset = ConnectionRuleParser.GetTargetKind(rule.Target.Trim()) == EConnectionTarget.Preset
            ? ConnectionPresets.Find(rule.Target.Trim())
            : null;
        if (preset?.ProbeUrls is { Count: > 0 } list)
        {
            return [.. list];
        }
        return ProbeUrl(rule) is { } single ? [single] : [];
    }

    /// <summary>Main URL of a rule: the first plain domain of the target or its preset.</summary>
    public static string? ProbeUrl(ConnectionRule rule)
    {
        var target = rule.Target.Trim();
        var domain = ConnectionRuleParser.GetTargetKind(target) switch
        {
            EConnectionTarget.Preset => ConnectionPresets.Find(target)?.Domains.FirstOrDefault(d => !d.Contains(':')),
            EConnectionTarget.Domain when !target.Contains(':') => target,
            _ => null,
        };
        return domain is null ? null : $"https://{domain}/";
    }

    private static string DisplayName(ConnectionRule rule)
    {
        return ConnectionPresets.Find(rule.Target)?.Name ?? rule.Target;
    }

    /// <summary>
    /// Fetches the URL over the direct path (direct + zapret), bypassing the VPN tunnel: the socket
    /// is bound to the physical network card's address, so TUN routes don't catch it. Success: the
    /// response ended, or enough bytes came through to rule out a DPI freeze. Any HTTP status
    /// counts, it only matters that data flows.
    /// </summary>
    public static Task<bool> ProbeAsync(string url, CancellationToken cancellationToken)
        => ProbeAsync(url, ZapretManager.UsesTpws, cancellationToken);

    /// <param name="viaTpws">macOS/Linux: through the tpws SOCKS proxy (the zapret path) or plain direct.</param>
    public static async Task<bool> ProbeAsync(string url, bool viaTpws, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_probeTimeout);
        try
        {
            var localAddress = PhysicalIPv4Address();
            // macOS/Linux: zapret is the tpws SOCKS proxy, so the check goes through it (tpws itself
            // connects directly: under TUN the cores route its process past the tunnel).
            var tpwsPort = viaTpws && ZapretManager.UsesTpws ? AppManager.Instance.Config.ZapretItem.SocksPort : 0;
            using var handler = tpwsPort > 0
                ? new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = new WebProxy($"socks5://127.0.0.1:{tpwsPort}"),
                    AllowAutoRedirect = true,
                }
                : new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = true,
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var addresses = await ResolveDirectAsync(ctx.DnsEndPoint.Host, localAddress, ct);
                        return await ConnectBoundAsync(addresses, ctx.DnsEndPoint.Port, localAddress, ct);
                    },
                };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0 Safari/537.36");

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[8192];
            var total = 0;
            while (total < ProbeBytes)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    return true;
                }
                total += read;
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<Stream> ConnectBoundAsync(IPAddress[] addresses, int port, IPAddress? localAddress, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (localAddress != null)
            {
                socket.Bind(new IPEndPoint(localAddress, 0));
            }
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>DNS-over-HTTPS endpoints addressed by IP, so resolving needs no DNS itself.</summary>
    private static readonly string[] _dohUrls =
    [
        "https://1.1.1.1/dns-query?type=A&name=",
        "https://8.8.8.8/resolve?type=A&name=",
    ];

    /// <summary>
    /// IPv4 addresses of <paramref name="host" /> for a direct connection. The system resolver is no
    /// good here: under TUN it returns fake IPs that only work inside the tunnel, and without TUN it
    /// is the ISP's DNS, which returns stub addresses for blocked domains. So DoH over the physical
    /// card first, the system resolver (minus fake IPs) as a last resort.
    /// </summary>
    internal static async Task<IPAddress[]> ResolveDirectAsync(string host, IPAddress? localAddress, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }
        foreach (var doh in _dohUrls)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectCallback = (ctx, c) => new ValueTask<Stream>(ConnectBoundAsync(
                        [IPAddress.Parse(ctx.DnsEndPoint.Host)], ctx.DnsEndPoint.Port, localAddress, c)),
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                using var req = new HttpRequestMessage(HttpMethod.Get, doh + Uri.EscapeDataString(host));
                req.Headers.Accept.ParseAdd("application/dns-json");
                using var resp = await client.SendAsync(req, ct);
                var ips = ParseDohAnswer(await resp.Content.ReadAsStringAsync(ct));
                if (ips.Length > 0)
                {
                    return ips;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // next resolver
            }
        }
        var system = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        return system.Where(a => !IsFakeIp(a)).ToArray();
    }

    /// <summary>A records from a DNS JSON API answer (Google/Cloudflare format).</summary>
    public static IPAddress[] ParseDohAnswer(string json)
    {
        try
        {
            var answer = JsonNode.Parse(json)?["Answer"]?.AsArray();
            return answer is null
                ? []
                : answer.Where(a => a?["type"]?.GetValue<int>() == 1)
                    .Select(a => IPAddress.TryParse(a!["data"]?.GetValue<string>(), out var ip) ? ip : null)
                    .Where(ip => ip != null && !IsFakeIp(ip))
                    .Select(ip => ip!)
                    .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>sing-box/Xray fake-IP range 198.18.0.0/15.</summary>
    private static bool IsFakeIp(IPAddress a)
    {
        return a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [198, 18 or 19, ..];
    }

    /// <summary>IPv4 address of the default physical network card (not the TUN adapter).</summary>
    private static IPAddress? PhysicalIPv4Address()
    {
        var name = NetworkUtils.GetDefaultPhysicalInterfaceName();
        if (name.IsNullOrEmpty())
        {
            return null;
        }
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => ni.Name == name)?
            .GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }
}
