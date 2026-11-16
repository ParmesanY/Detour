namespace ServiceLib.Manager;

/// <summary>What one site did on each path, and the rule that fits.</summary>
/// <param name="Direct">Plain direct path (bound to the network card, past the tunnel); null = not checked.</param>
/// <param name="Zapret">Direct path through zapret; null = not checked.</param>
/// <param name="Vpn">Through Detour's local proxy (the VPN for a site without a rule); null = not checked.</param>
/// <param name="Suggested">Rule to add; null when nothing helps (the site is down or refuses this VPN too).</param>
public sealed record SiteCheckResult(
    string Host,
    bool? Direct,
    bool? Zapret,
    bool? Vpn,
    EConnectionMode? Suggested,
    EConnectionMode? Current,
    string Verdict)
{
    public bool ChangeSuggested => Suggested is { } s && s != Current && !(Current is null && s == EConnectionMode.Vpn);
}

/// <summary>
/// "A site doesn't open": checks one address directly, through zapret and through the VPN, and suggests
/// the rule. zapret on Windows only handles hosts from its lists, so for the check the site is put into
/// zapret's list for a moment (winws restarts twice).
/// </summary>
public static class SiteCheckManager
{
    private const int _probeBytes = 24 * 1024;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(12);

    /// <summary>"https://www.Site.com/path" → "site.com"; null when it isn't a domain.</summary>
    public static string? NormalizeHost(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return null;
        }
        if (!s.Contains("://"))
        {
            s = "https://" + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || !uri.IdnHost.Contains('.') || IPAddress.TryParse(uri.Host, out _))
        {
            return null;
        }
        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// <summary>The user's enabled rule that covers the host: its domain or a preset listing it.</summary>
    public static ConnectionRule? FindRule(IEnumerable<ConnectionRule>? rules, string host)
    {
        static bool Covers(string domain, string host) =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

        return (rules ?? []).FirstOrDefault(r =>
        {
            if (!r.Enabled)
            {
                return false;
            }
            var target = r.Target.Trim();
            return ConnectionRuleParser.GetTargetKind(target) switch
            {
                EConnectionTarget.Domain => Covers(target, host),
                EConnectionTarget.Preset => ConnectionPresets.Find(target)?.Domains.Any(d => !d.Contains(':') && Covers(d, host)) == true,
                _ => false,
            };
        });
    }

    public static async Task<SiteCheckResult> CheckAsync(Config config, string host, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var url = $"https://{host}/";
        var current = FindRule(config.ConnectionRules, host)?.Mode;
        var zapret = ZapretManager.Instance;
        // While winws runs and the site is already in its list, the direct probe is the zapret path.
        var zapretCovers = zapret.IsRunning && !ZapretManager.UsesTpws && current is EConnectionMode.Zapret or EConnectionMode.ZapretThenVpn;

        progress?.Report(AppText.SiteChecking);
        var first = await FetchAsync(DirectHandler(), url, ct);
        int? direct = zapretCovers ? null : first;
        int? viaZapret = zapretCovers ? first : null;

        if (first == NoData && !zapretCovers && zapret.IsRunning && !zapret.IsSelecting)
        {
            progress?.Report(AppText.SiteCheckingZapret);
            viaZapret = ZapretManager.UsesTpws
                ? await FetchAsync(SocksHandler(config.ZapretItem.SocksPort), url, ct)
                : await FetchWithWinwsAsync(config, host, url, ct);
        }

        // The VPN path tells "blocked" from "the site is down"; with a non-VPN rule the proxy wouldn't use the VPN.
        int? vpn = null;
        if (current is null or EConnectionMode.Vpn && !Works(direct) && !Works(viaZapret))
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            if (await IsListeningAsync(port, ct))
            {
                progress?.Report(AppText.SiteCheckingVpn);
                vpn = await FetchAsync(SocksHandler(port), url, ct);
            }
        }
        return Evaluate(host, current, direct, viaZapret, vpn);
    }

    /// <summary>Status of a path that was checked but passed no data.</summary>
    public const int NoData = 0;

    /// <summary>403 / 451: data flows, but the site turns Russian addresses away.</summary>
    private static bool Refused(int? status) => status is 403 or 451;

    private static bool Works(int? status) => status is { } s && s != NoData && !Refused(s);

    /// <param name="direct">HTTP status per path: null = not checked, <see cref="NoData"/> = nothing came through.</param>
    public static SiteCheckResult Evaluate(string host, EConnectionMode? current, int? direct, int? zapret, int? vpn)
    {
        static bool? Ok(int? s) => s is null ? null : Works(s);

        EConnectionMode? mode;
        string verdict;
        if (Refused(direct) && Works(vpn))
        {
            (mode, verdict) = (EConnectionMode.Vpn, AppText.CheckRefusesRussia);
        }
        else if (Works(direct))
        {
            // Direct where the user chose zapret: keep zapret, it only helps.
            (mode, verdict) = (current is EConnectionMode.Zapret or EConnectionMode.ZapretThenVpn ? current : EConnectionMode.Direct,
                AppText.CheckWorksDirect);
        }
        else if (Works(zapret))
        {
            (mode, verdict) = (current == EConnectionMode.Zapret ? current : EConnectionMode.ZapretThenVpn, AppText.CheckWorksZapret);
        }
        else if (Works(vpn))
        {
            (mode, verdict) = (current == EConnectionMode.Warp ? current : EConnectionMode.Vpn, AppText.SiteVpnOnly);
        }
        else if (vpn is not null)
        {
            (mode, verdict) = (null, AppText.SiteDown);
        }
        else
        {
            (mode, verdict) = (current == EConnectionMode.Warp ? current : EConnectionMode.Vpn,
                zapret is null ? AppText.CheckBlockedNoZapret : AppText.CheckBlocked);
        }
        return new(host, Ok(direct), Ok(zapret), Ok(vpn), mode, current, verdict);
    }

    private static async Task<int?> FetchWithWinwsAsync(Config config, string host, string url, CancellationToken ct)
    {
        var compiled = ConnectionRulesManager.Instance.Compile(config);
        try
        {
            ZapretManager.WriteUserLists([.. compiled.ZapretDomains, host], compiled.ZapretIps,
                compiled.ZapretExcludeDomains.Where(d => !host.Equals(d, StringComparison.OrdinalIgnoreCase)
                                                         && !host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)),
                await ZapretManager.GetVpnServerIpsAsync());
            var (ok, _) = await ZapretManager.Instance.StartAsync();
            if (!ok)
            {
                return null; // not checked
            }
            // Let winws hook in before measuring.
            await Task.Delay(1500, ct);
            return await FetchAsync(DirectHandler(), url, ct);
        }
        finally
        {
            // Back to the lists of the saved rules.
            await ConnectionRulesManager.Instance.SyncZapretAsync(config);
        }
    }

    private static SocketsHttpHandler DirectHandler()
    {
        var localAddress = ZapretWatchdog.PhysicalIPv4Address();
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await ZapretWatchdog.ResolveDirectAsync(ctx.DnsEndPoint.Host, localAddress, ct);
                return await ZapretWatchdog.ConnectBoundAsync(addresses, ctx.DnsEndPoint.Port, localAddress, ct);
            },
        };
    }

    private static SocketsHttpHandler SocksHandler(int port) => new()
    {
        UseProxy = true,
        Proxy = new WebProxy($"socks5://{Global.Loopback}:{port}"),
        AllowAutoRedirect = true,
    };

    /// <summary>HTTP status when enough data came through (past a DPI freeze), <see cref="NoData"/> when it didn't.</summary>
    private static async Task<int> FetchAsync(SocketsHttpHandler handler, string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0 Safari/537.36");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[8192];
            var total = 0;
            while (total < _probeBytes)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NoData;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            return NoData;
        }
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            await tcp.ConnectAsync(Global.Loopback, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
