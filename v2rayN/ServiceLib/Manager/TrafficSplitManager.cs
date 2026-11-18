namespace ServiceLib.Manager;

/// <summary>Bytes by path: through the VPN, past it directly, past it via zapret. Today and since the counter started.</summary>
public class TrafficSplitStats
{
    /// <summary>yyyy-MM-dd of the Today* values.</summary>
    public string Day { get; set; } = string.Empty;

    public long TodayVpn { get; set; }
    public long TodayDirect { get; set; }
    public long TodayZapret { get; set; }
    public long TotalVpn { get; set; }
    public long TotalDirect { get; set; }
    public long TotalZapret { get; set; }
    public DateTime? Since { get; set; }

    public void Add(long vpn, long direct, long zapret, DateTime now)
    {
        var day = now.ToString("yyyy-MM-dd");
        if (Day != day)
        {
            Day = day;
            TodayVpn = TodayDirect = TodayZapret = 0;
        }
        Since ??= now;
        TodayVpn += vpn;
        TodayDirect += direct;
        TodayZapret += zapret;
        TotalVpn += vpn;
        TotalDirect += direct;
        TotalZapret += zapret;
    }
}

/// <summary>
/// Splits sing-box's traffic counters by path. The clash API lists open connections with their outbound
/// and bytes; connections that open and close between two polls are not seen, so the exact global
/// total is spread over the paths in the proportion of what was seen.
/// </summary>
public class TrafficSplitTracker
{
    private readonly Dictionary<string, ulong> _seen = [];
    private ulong _lastTotal;
    private bool _started;
    private (double Vpn, double Direct, double Zapret) _ratio = (1, 0, 0);

    public enum Path { Vpn, Direct, Zapret, None }

    public static Path Classify(ConnectionItem c, Func<string, bool> isZapretHost)
    {
        var chains = c.chains ?? [];
        if (chains.Count == 0 || chains.Any(t => t == Global.BlockTag || t.StartsWith("dns", StringComparison.OrdinalIgnoreCase)))
        {
            return Path.None;
        }
        if (chains.Any(t => t.StartsWith(Global.ProxyTag, StringComparison.OrdinalIgnoreCase)))
        {
            return Path.Vpn;
        }
        var host = c.metadata?.host.IsNullOrEmpty() == false ? c.metadata.host : c.metadata?.sniffHost;
        return host.IsNotEmpty() && isZapretHost(host!) ? Path.Zapret : Path.Direct;
    }

    /// <summary>One poll: bytes per path since the previous one. The first poll only sets the baseline.</summary>
    public (long Vpn, long Direct, long Zapret) Update(ClashConnections snapshot, Func<string, bool> isZapretHost)
    {
        var total = snapshot.uploadTotal + snapshot.downloadTotal;
        var seen = new double[3];
        var alive = new HashSet<string>();
        foreach (var c in snapshot.connections ?? [])
        {
            if (c.id.IsNullOrEmpty())
            {
                continue;
            }
            alive.Add(c.id);
            var bytes = c.upload + c.download;
            var before = _seen.GetValueOrDefault(c.id);
            _seen[c.id] = bytes;
            var path = Classify(c, isZapretHost);
            if (path != Path.None && bytes > before && _started)
            {
                seen[(int)path] += bytes - before;
            }
        }
        foreach (var gone in _seen.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _seen.Remove(gone);
        }

        // A core restart resets the counters.
        if (!_started || total < _lastTotal)
        {
            _started = true;
            _lastTotal = total;
            return (0, 0, 0);
        }
        var delta = (double)(total - _lastTotal);
        _lastTotal = total;
        var seenSum = seen.Sum();
        if (seenSum > 0)
        {
            _ratio = (seen[0] / seenSum, seen[1] / seenSum, seen[2] / seenSum);
        }
        // What was seen counts as is; the rest goes by the latest proportion.
        var rest = Math.Max(0, delta - seenSum);
        return ((long)(seen[0] + rest * _ratio.Vpn), (long)(seen[1] + rest * _ratio.Direct), (long)(seen[2] + rest * _ratio.Zapret));
    }
}

/// <summary>Counts how much traffic went past the VPN (directly and through zapret): the VPN subscription's savings.</summary>
public sealed class TrafficSplitManager
{
    private static readonly Lazy<TrafficSplitManager> _instance = new(() => new());
    public static TrafficSplitManager Instance => _instance.Value;

    private const string _tag = "TrafficSplitManager";
    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _saveEvery = TimeSpan.FromMinutes(1);
    private static string StatsPath => Utils.GetConfigPath("trafficSplit.json");

    private readonly object _gate = new();
    private TrafficSplitStats _stats = new();
    private TrafficSplitTracker _tracker = new();
    private int _started;

    /// <summary>Raised after the numbers changed (background thread).</summary>
    public event Action? Changed;

    public TrafficSplitStats Snapshot()
    {
        lock (_gate)
        {
            _stats.Add(0, 0, 0, DateTime.Now); // rolls the day over
            return JsonUtils.DeepCopy(_stats) ?? new();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _stats = new();
        }
        Save();
        Changed?.Invoke();
    }

    public void Start(Config config)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        try
        {
            if (File.Exists(StatsPath))
            {
                _stats = JsonUtils.Deserialize<TrafficSplitStats>(File.ReadAllText(StatsPath)) ?? new();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        Task.Run(() => RunAsync(config));
    }

    private async Task RunAsync(Config config)
    {
        var lastSave = DateTime.Now;
        var dirty = false;
        HashSet<string> zapretDomains = [];
        var domainsAt = DateTime.MinValue;
        while (true)
        {
            try
            {
                await Task.Delay(_poll).ConfigureAwait(false);
                if (!AppManager.Instance.IsRunningCore(ECoreType.sing_box))
                {
                    _tracker = new();
                    continue;
                }
                if (DateTime.Now - domainsAt > TimeSpan.FromSeconds(30))
                {
                    zapretDomains = [.. ZapretManager.Instance.IsRunning
                        ? ConnectionRulesManager.Instance.Compile(config).ZapretDomains.Select(PlainDomain)
                        : []];
                    domainsAt = DateTime.Now;
                }
                var snapshot = await FetchAsync().ConfigureAwait(false);
                if (snapshot is null)
                {
                    continue;
                }
                var (vpn, direct, zapret) = _tracker.Update(snapshot, host => IsUnder(host, zapretDomains));
                if (vpn + direct + zapret > 0)
                {
                    lock (_gate)
                    {
                        _stats.Add(vpn, direct, zapret, DateTime.Now);
                    }
                    dirty = true;
                    Changed?.Invoke();
                }
                if (dirty && DateTime.Now - lastSave > _saveEvery)
                {
                    Save();
                    lastSave = DateTime.Now;
                    dirty = false;
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    private static async Task<ClashConnections?> FetchAsync()
    {
        try
        {
            using var httpClient = ClashApiManager.NewClient();
            var json = await httpClient.GetStringAsync($"{Global.HttpProtocol}{Global.Loopback}:{AppManager.Instance.StatePort2}/connections");
            return JsonUtils.Deserialize<ClashConnections>(json);
        }
        catch
        {
            return null; // the core is (re)starting
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate)
            {
                json = JsonUtils.Serialize(_stats);
            }
            File.WriteAllText(StatsPath, json);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>"domain:x.com" / "full:x.com" / "x.com" → "x.com".</summary>
    private static string PlainDomain(string d)
    {
        var i = d.IndexOf(':');
        return (i >= 0 ? d[(i + 1)..] : d).Trim().TrimStart('.').ToLowerInvariant();
    }

    public static bool IsUnder(string host, ISet<string> domains)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        while (true)
        {
            if (domains.Contains(host))
            {
                return true;
            }
            var dot = host.IndexOf('.');
            if (dot < 0)
            {
                return false;
            }
            host = host[(dot + 1)..];
        }
    }
}
