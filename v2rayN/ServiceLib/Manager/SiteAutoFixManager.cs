namespace ServiceLib.Manager;

/// <summary>
/// Sites fix themselves when opened. Under TUN (sing-box) Detour sees every connection; a site whose
/// connections keep failing (the app sent data, nothing came back) while none of them succeeds is checked
/// directly, through zapret and through the VPN — the same check as "A site doesn't open" — and:
/// <list type="bullet">
/// <item>a site without a rule gets one at the top of the rules (e.g. a Russian site that refuses the VPN's
/// foreign address goes direct), with a notice;</item>
/// <item>a site the user has a rule for (their own or a preset) only gets a notice with the suggestion: the
/// user's rules are never changed behind their back.</item>
/// </list>
/// At most one check every 3 minutes (the zapret check restarts winws), each site once a day, 10 a day.
/// </summary>
public sealed class SiteAutoFixManager
{
    private static readonly Lazy<SiteAutoFixManager> _instance = new(() => new());
    public static SiteAutoFixManager Instance => _instance.Value;
    private const string _tag = "SiteAutoFix";

    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(3);
    public const int MaxPerDay = 10;

    private readonly Detector _detector = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly List<DateTime> _checks = [];
    private DateTime _lastCheck = DateTime.MinValue;

    /// <summary>Raised after Detour added a rule by itself: the rules page reloads if it has no unsaved edits.</summary>
    public event Action? RulesChanged;

    public void Start()
    {
        StallWatchManager.Instance.ConnectionsPolled += OnPolled;
    }

    private void OnPolled(DateTime now, IReadOnlyList<ConnectionItem> connections)
    {
        var config = AppManager.Instance.Config;
        if (config.AutoFixSites == false)
        {
            _detector.Reset();
            return;
        }
        foreach (var host in _detector.Observe(now, connections.Select(ToSample)))
        {
            _checks.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (now - _lastCheck < MinGap || _checks.Count >= MaxPerDay || !_busy.Wait(0))
            {
                _detector.Forget(host); // try again when it fails again later
                continue;
            }
            _lastCheck = now;
            _checks.Add(now);
            _ = Task.Run(async () =>
            {
                try
                {
                    await FixAsync(config, host);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
                finally
                {
                    _busy.Release();
                }
            });
        }
    }

    private async Task FixAsync(Config config, string host)
    {
        Logging.SaveLog($"{_tag}: {host} keeps failing on its path, checking the others");
        var r = await SiteCheckManager.CheckAsync(config, host);
        Logging.SaveLog($"{_tag}: {host}: direct={r.Direct} zapret={r.Zapret} vpn={r.Vpn} -> {r.Suggested} (rule: {r.Current})");
        if (r.Suggested is not { } mode || !r.ChangeSuggested)
        {
            return;
        }
        if (r.Current is { } current)
        {
            NoticeManager.Instance.Enqueue(string.Format(AppText.AutoFixSuggest, host, AppText.ModeName(current), AppText.ModeName(mode)));
            return;
        }
        config.ConnectionRules ??= [];
        if (config.ConnectionRules.Any(x => x.Target.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        config.ConnectionRules.Insert(0, new ConnectionRule { Target = host, Mode = mode });
        await ConfigHandler.SaveConfig(config);
        Logging.SaveLog($"{_tag}: rule added: {host} {mode}");
        NoticeManager.Instance.Enqueue(string.Format(AppText.AutoFixApplied, host, AppText.ModeName(mode)));
        RulesChanged?.Invoke();
        AppEvents.CoreReloadRequested.Publish();
    }

    private static Detector.Sample ToSample(ConnectionItem c)
    {
        var chains = c.chains ?? [];
        return new(c.id ?? "", SiteCheckManager.NormalizeHost(c.metadata?.host), (long)c.upload, (long)c.download,
            chains.Any(x => x.Equals(Global.BlockTag, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Connection outcomes per site; pure, for tests.</summary>
    public sealed class Detector
    {
        public record Sample(string Id, string? Host, long Upload, long Download, bool Blocked);

        public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan ReportEvery = TimeSpan.FromDays(1);
        public const int FailuresToReport = 3;
        /// <summary>Sites that must be working in the window for a failing one to count as the site's problem.</summary>
        public const int HealthyHostsNeeded = 3;
        /// <summary>Less than this back after sending: the handshake didn't complete (reset, timeout, stub).</summary>
        public const long FailedBelow = 1024;
        /// <summary>At least this back: the site works on this path.</summary>
        public const long WorksFrom = 4096;

        private sealed class Track
        {
            public required string Host;
            public long Up, Down;
            public DateTime FirstSeen;
            public bool Counted;
        }

        private readonly Dictionary<string, Track> _open = new();
        private readonly Dictionary<string, List<(DateTime At, bool Ok)>> _events = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _reported = new(StringComparer.OrdinalIgnoreCase);

        public void Reset()
        {
            _open.Clear();
            _events.Clear();
        }

        public void Forget(string host)
        {
            _reported.Remove(host);
            _events.Remove(host);
        }

        /// <summary>Takes the current connections; returns the sites to check now.</summary>
        public List<string> Observe(DateTime now, IEnumerable<Sample> samples)
        {
            var seen = new HashSet<string>();
            foreach (var s in samples)
            {
                if (s.Id.Length == 0 || s.Host is null || s.Blocked)
                {
                    continue;
                }
                seen.Add(s.Id);
                if (!_open.TryGetValue(s.Id, out var t))
                {
                    _open[s.Id] = t = new Track { Host = s.Host, FirstSeen = now };
                }
                t.Up = s.Upload;
                t.Down = s.Download;
                if (t.Counted)
                {
                    continue;
                }
                if (t.Down >= WorksFrom)
                {
                    Add(t.Host, now, true);
                    t.Counted = true;
                }
                else if (t.Down == 0 && t.Up > 0 && now - t.FirstSeen >= StuckAfter)
                {
                    Add(t.Host, now, false);
                    t.Counted = true;
                }
            }
            foreach (var (id, t) in _open.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                _open.Remove(id);
                if (!t.Counted && t.Up > 0)
                {
                    // Closed: a short answer (a redirect, a 204) still counts as working.
                    Add(t.Host, now, t.Down >= FailedBelow);
                }
            }

            var result = new List<string>();
            foreach (var (host, list) in _events.ToList())
            {
                list.RemoveAll(e => now - e.At > Window);
                if (list.Count == 0)
                {
                    _events.Remove(host);
                }
            }
            // When the VPN (or the internet) is down every site fails at once: that's not a site to fix, and a
            // check would happily find foreign sites "working directly". Only while other sites work.
            if (_events.Count(kv => kv.Value.Any(e => e.Ok)) < HealthyHostsNeeded)
            {
                return result;
            }
            foreach (var (host, list) in _events)
            {
                if (list.Count(e => !e.Ok) >= FailuresToReport && list.All(e => !e.Ok)
                    && (!_reported.TryGetValue(host, out var at) || now - at > ReportEvery))
                {
                    _reported[host] = now;
                    result.Add(host);
                }
            }
            return result;
        }

        private void Add(string host, DateTime at, bool ok)
        {
            if (!_events.TryGetValue(host, out var list))
            {
                _events[host] = list = [];
            }
            list.Add((at, ok));
        }
    }
}
