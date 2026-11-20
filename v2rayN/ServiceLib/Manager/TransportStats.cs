using Detour.NetProbe;

namespace ServiceLib.Manager;

/// <summary>
/// VPN transports by ISP, the Detour side of Detour Map's <c>/v1/transports</c>.
/// <list type="bullet">
/// <item>Out (only with the block map opt-in): how the VPN connection fared per transport kind — checks that
/// passed, froze (data stopped after a few KB) or failed — sent at most once an hour. Only the kind is sent
/// ("vless-reality", "hysteria2"…), never a server address, name or provider.</item>
/// <item>In (with registry hints on): which transports work on this ISP, so failover tries those first.</item>
/// </list>
/// </summary>
public sealed class TransportStats
{
    private static readonly Lazy<TransportStats> _instance = new(() => new());
    public static TransportStats Instance => _instance.Value;
    private const string _tag = "TransportStats";

    private static readonly TimeSpan SendEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan HintsEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public enum Outcome { Ok, Frozen, Fail }

    private readonly Dictionary<string, TransportResult> _pending = new();
    private readonly object _lock = new();
    private DateTime _lastSent = DateTime.Now;
    private DateTime _hintsAt = DateTime.MinValue;
    private Dictionary<string, TransportStat> _hints = new();

    /// <summary>The transport kind of a server, one of <see cref="TransportReport.Kinds"/>.</summary>
    public static string KindOf(ProfileItem? p)
    {
        if (p == null)
        {
            return "other";
        }
        if (p.IndexId == "detour-warp" || p.Remarks == Global.WarpOutboundRemarks)
        {
            return "warp";
        }
        if (p.ConfigType == EConfigType.Custom)
        {
            return CustomKind(p);
        }
        return Kind(p.ConfigType, p.Network, p.StreamSecurity, p.ConfigType == EConfigType.WireGuard && AmneziaCore.IsAmnezia(p));
    }

    public static string Kind(EConfigType type, string? network, string? security, bool amnezia = false)
    {
        network = (network ?? "").ToLowerInvariant();
        security = (security ?? "").ToLowerInvariant();
        return type switch
        {
            EConfigType.VLESS when security == Global.StreamSecurityReality => network is "xhttp" or "splithttp" ? "vless-reality-xhttp" : "vless-reality",
            EConfigType.VLESS when network is "xhttp" or "splithttp" => "vless-xhttp",
            EConfigType.VLESS when network == "ws" => "vless-ws",
            EConfigType.VLESS when network == "grpc" => "vless-grpc",
            EConfigType.VLESS when security == Global.StreamSecurity => "vless-tls",
            EConfigType.VMess => "vmess",
            EConfigType.Trojan => "trojan",
            EConfigType.Shadowsocks => "shadowsocks",
            EConfigType.Hysteria2 => "hysteria2",
            EConfigType.TUIC => "tuic",
            EConfigType.WireGuard => amnezia ? "amneziawg" : "wireguard",
            _ => "other",
        };
    }

    /// <summary>A custom (full Xray JSON) profile: the kind of its proxy outbound.</summary>
    private static string CustomKind(ProfileItem p)
    {
        try
        {
            var path = File.Exists(p.Address) ? p.Address : Utils.GetConfigPath(p.Address);
            if (!File.Exists(path) || JsonUtils.ParseJson(File.ReadAllText(path)) is not JsonObject root
                || root["outbounds"] is not JsonArray outbounds
                || CustomConfigRuleServers.ProxyOutbounds(outbounds).FirstOrDefault() is not { } proxy)
            {
                return "other";
            }
            var type = proxy["protocol"]?.ToString()?.ToLowerInvariant() switch
            {
                "vless" => EConfigType.VLESS,
                "vmess" => EConfigType.VMess,
                "trojan" => EConfigType.Trojan,
                "shadowsocks" => EConfigType.Shadowsocks,
                "hysteria" or "hysteria2" => EConfigType.Hysteria2,
                "wireguard" => EConfigType.WireGuard,
                _ => EConfigType.Custom,
            };
            var stream = proxy["streamSettings"];
            return Kind(type, stream?["network"]?.ToString(), stream?["security"]?.ToString());
        }
        catch
        {
            return "other";
        }
    }

    /// <summary>Counts one check of the current server's transport.</summary>
    public void Record(string kind, Outcome outcome)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(kind, out var r))
            {
                _pending[kind] = r = new TransportResult { Transport = kind };
            }
            switch (outcome)
            {
                case Outcome.Ok: r.Ok++; break;
                case Outcome.Frozen: r.Frozen++; break;
                default: r.Fail++; break;
            }
        }
        _ = FlushIfDueAsync();
    }

    /// <summary>The report to send, or null when there's nothing (or sharing is off). Clears the counters.</summary>
    public TransportReport? TakeReport(Config config, bool force = false)
    {
        lock (_lock)
        {
            if (_pending.Count == 0 || (!force && DateTime.Now - _lastSent < SendEvery))
            {
                return null;
            }
            var results = _pending.Values.Where(r => r.Ok + r.Frozen + r.Fail > 0).ToList();
            _pending.Clear();
            _lastSent = DateTime.Now;
            if (!config.BlockMapItem.Enabled || results.Count == 0)
            {
                return null;
            }
            return new TransportReport
            {
                Client = BlockMapManager.Client,
                Net = config.BlockMapItem.Net,
                // zapret tricks on the VPN server's packets: the result measures the trick, the collector refuses it.
                Env = new ProbeEnv { DpiBypass = VpnStrategyManager.Find(config.ZapretItem.VpnStrategy)?.UsesWinws == true, Nic = true },
                Results = results.Select(r => new TransportResult { Transport = r.Transport, Ok = Math.Min(r.Ok, 200), Frozen = Math.Min(r.Frozen, 200), Fail = Math.Min(r.Fail, 200) }).ToList(),
            };
        }
    }

    private async Task FlushIfDueAsync()
    {
        var config = AppManager.Instance.Config;
        var report = TakeReport(config);
        if (report == null || report.Env.DpiBypass)
        {
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var sent = await Transports.SendAsync(report, new Uri(BlockMapManager.Endpoint), NicInfo.Detect(), cts.Token);
            Logging.SaveLog($"{_tag}: sent {report.Results.Count} transports: {(sent.Accepted ? "accepted" : $"{sent.HttpStatus} {sent.Error}")}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: not sent: {ex.Message}");
        }
    }

    /// <summary>Share of clean checks of a transport on this ISP (0..1), or null when the map doesn't know.</summary>
    public double? Score(string kind) => _hints.TryGetValue(kind, out var s) ? s.Ok : null;

    /// <summary>Refreshes the ISP's transports from the map at most once an hour (registry hints on).</summary>
    public async Task RefreshHintsAsync(Config config, CancellationToken ct)
    {
        if (!config.ZapretItem.RegistryHints || DateTime.Now - _hintsAt < HintsEvery)
        {
            return;
        }
        _hintsAt = DateTime.Now;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var hints = await Transports.GetAsync(new Uri(BlockMapManager.Endpoint), NicInfo.Detect(), cts.Token);
            if (hints != null)
            {
                _hints = hints.Transports.ToDictionary(t => t.Transport, t => t);
                Logging.SaveLog($"{_tag}: {hints.Transports.Count} transports known for {hints.AsOrg ?? $"AS{hints.Asn}"}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: hints unavailable: {ex.Message}");
        }
    }
}
