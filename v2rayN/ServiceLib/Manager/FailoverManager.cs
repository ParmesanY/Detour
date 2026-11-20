using System.Net;

namespace ServiceLib.Manager;

/// <summary>
/// Auto failover: while a VPN server is active, a request goes through the local proxy every 30 s.
/// After 3 failures in a row, and only if the internet itself works (a Russian site over the physical
/// card, bypassing TUN), the next server of the same group is picked: lowest last ping first, then
/// list order. Servers tried in the last 10 minutes are skipped, and at most 5 switches happen in that
/// window, so a dead network or a dead subscription doesn't cycle the list forever.
/// </summary>
public sealed class FailoverManager
{
    private static readonly Lazy<FailoverManager> _instance = new(() => new());
    public static FailoverManager Instance => _instance.Value;
    private const string _tag = "Failover";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int FailuresToSwitch = 3;
    private const int MaxSwitchesPerWindow = 5;
    private const string ProbeUrl = "https://www.gstatic.com/generate_204";
    private const string InternetProbeUrl = "https://ya.ru/";

    private readonly Dictionary<string, DateTime> _tried = new();
    private readonly List<DateTime> _switches = [];
    private CancellationTokenSource? _cts;
    private int _failures;
    private bool _heldNotified;
    private bool _whitelistNotified;
    private bool _frozenNotified;
    private int _ticks;

    /// <summary>A 64 KB download every 10 ticks (5 min): the TSPU freeze stops TCP after ~15-20 KB.</summary>
    private const int BulkEvery = 10;
    private const int BulkBytes = 64 * 1024;
    private const string BulkProbeUrl = "https://speed.cloudflare.com/__down?bytes=65536";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);
    private DateTime _quietUntil = DateTime.MinValue;

    /// <summary>Raised (any thread) with the server to switch to; the UI applies it like a manual pick.</summary>
    public event Action<string, string>? SwitchRequested;

    public void Start()
    {
        if (_cts != null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Called after every core (re)start: give the new server time to come up.</summary>
    public void NotifyReloaded()
    {
        _failures = 0;
        _quietUntil = DateTime.Now + Grace;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, token);
                await TickAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var config = AppManager.Instance.Config;
        if (!config.AutoFailover || config.IndexId.IsNullOrEmpty() || DateTime.Now < _quietUntil)
        {
            return;
        }

        var alive = await ProbeProxyAsync(token);
        // A server the ISP freezes still answers small requests: every few ticks pull 64 KB too.
        if (alive && (++_ticks % BulkEvery == 0 || _failures > 0))
        {
            var (ok, received) = await ProbeBulkAsync(token);
            TransportStats.Instance.Record(await CurrentKindAsync(config), ok ? TransportStats.Outcome.Ok : TransportStats.Outcome.Frozen);
            if (!ok)
            {
                alive = false;
                Logging.SaveLog($"{_tag}: data stops after {received / 1024} KB through the VPN: frozen");
                if (!_frozenNotified)
                {
                    _frozenNotified = true;
                    NoticeManager.Instance.Enqueue(string.Format(AppText.FailoverFrozen, received / 1024));
                }
            }
        }
        if (alive)
        {
            _failures = 0;
            _heldNotified = false;
            _frozenNotified = false;
            return;
        }
        _failures++;
        Logging.SaveLog($"{_tag}: probe through the VPN failed ({_failures}/{FailuresToSwitch})");
        if (_failures < FailuresToSwitch)
        {
            return;
        }

        if (!await ZapretWatchdog.ProbeInternetAsync(InternetProbeUrl, token))
        {
            Logging.SaveLog($"{_tag}: the direct path is down too, no internet: not switching");
            _failures = 0;
            return;
        }

        var now = DateTime.Now;
        if (_failures == FailuresToSwitch)
        {
            // The internet works and the VPN doesn't: one failed check of this transport (once per episode).
            TransportStats.Instance.Record(await CurrentKindAsync(config), TransportStats.Outcome.Fail);
        }

        // Russian sites open directly, foreign ones don't even directly: a white list (mobile networks, by IP).
        // Going through the server list then only burns time; only a relay on a Russian IP can get through.
        var whitelist = await LooksLikeWhitelistAsync(token);
        if (whitelist)
        {
            Logging.SaveLog($"{_tag}: Russian sites open directly, foreign ones don't: looks like a white list");
        }
        else
        {
            _whitelistNotified = false;
        }

        // A new server mid-game means a new IP: the match drops and anti-fraud may ban the account.
        if (GameModeManager.Instance.HoldsServer)
        {
            Logging.SaveLog($"{_tag}: {GameModeManager.Instance.ActiveGame} is running, not switching");
            if (!_heldNotified)
            {
                _heldNotified = true;
                NoticeManager.Instance.Enqueue(string.Format(AppText.GameModeHeldServer, GameModeManager.Instance.ActiveGame));
            }
            _failures = 0;
            return;
        }
        _heldNotified = false;

        _switches.RemoveAll(t => now - t > Window);
        foreach (var key in _tried.Where(kv => now - kv.Value > Window).Select(kv => kv.Key).ToList())
        {
            _tried.Remove(key);
        }
        if (_switches.Count >= MaxSwitchesPerWindow)
        {
            Logging.SaveLog($"{_tag}: {MaxSwitchesPerWindow} switches in {Window.TotalMinutes} min, pausing");
            _quietUntil = now + Window;
            return;
        }

        _tried[config.IndexId] = now;
        var current = await AppManager.Instance.GetProfileItem(config.IndexId);
        var group = await AppManager.Instance.ProfileItems(current?.Subid ?? config.SubIndexId) ?? [];
        var exs = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(e => e.IndexId, e => e);
        // Servers on hosting networks Russian ISPs freeze as a whole go last (within the same country).
        HashSet<string> frozen;
        try
        {
            using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // The current server first: its AS is needed to prefer another network.
            frozen = await ServerNetworkCheck.FrozenServersAsync(group.OrderBy(p => p.IndexId == config.IndexId ? 0 : 1), lookup.Token);
            frozen.Remove(config.IndexId);
        }
        catch (OperationCanceledException)
        {
            frozen = [];
        }
        var currentHost = ServerHost(current);
        var currentAsn = ServerNetworkCheck.CachedAsn(current?.Address);
        await TransportStats.Instance.RefreshHintsAsync(config, token);
        var candidates = group.Select(p => new Candidate(p.IndexId,
                exs.TryGetValue(p.IndexId, out var ex) ? ex.Delay : 0,
                exs.TryGetValue(p.IndexId, out var ex2) ? ex2.Sort : 0,
                ServerNameFormatter.ExitCountry(p.Remarks),
                frozen.Contains(p.IndexId),
                ServerNameFormatter.IsRussianRelay(p.Remarks),
                currentHost != null && string.Equals(ServerHost(p), currentHost, StringComparison.OrdinalIgnoreCase),
                currentAsn > 0 && ServerNetworkCheck.CachedAsn(p.Address) == currentAsn,
                TransportStats.Instance.Score(TransportStats.KindOf(p))))
            .ToList();
        var next = whitelist
            ? PickNextUnderWhitelist(candidates, config.IndexId, _tried.Keys)
            : PickNext(candidates, config.IndexId, _tried.Keys, ServerNameFormatter.ExitCountry(current?.Remarks));
        if (next == null && whitelist)
        {
            Logging.SaveLog($"{_tag}: white list and no Russian relay to try: pausing");
            if (!_whitelistNotified)
            {
                _whitelistNotified = true;
                NoticeManager.Instance.Enqueue(AppText.FailoverWhitelist);
            }
            _quietUntil = now + Window;
            return;
        }
        if (next == null)
        {
            Logging.SaveLog($"{_tag}: no other server to try");
            _quietUntil = now + Window;
            return;
        }

        _switches.Add(now);
        _failures = 0;
        _quietUntil = now + Grace;
        var nextItem = group.First(p => p.IndexId == next);
        Logging.SaveLog($"{_tag}: switching to {nextItem.Remarks}");
        SwitchRequested?.Invoke(next, nextItem.Remarks);
    }

    public record Candidate(string IndexId, int Delay, int Sort, string? Country = null, bool FrozenNetwork = false, bool RussianRelay = false,
        bool SameHost = false, bool SameNetwork = false, double? TransportScore = null);

    /// <summary>Detour Map: a transport that passes in fewer than this share of checks on this ISP goes later.</summary>
    public const double PoorTransport = 0.5;

    private static async Task<string> CurrentKindAsync(Config config)
        => TransportStats.KindOf(await AppManager.Instance.GetProfileItem(config.IndexId));

    /// <summary>
    /// The server's host: the address, or for a custom (full JSON) profile the address of its proxy
    /// outbound. Two profiles with one host are one machine with different transports.
    /// </summary>
    public static string? ServerHost(ProfileItem? p)
    {
        if (p == null)
        {
            return null;
        }
        if (p.ConfigType != EConfigType.Custom)
        {
            return p.Address.IsNullOrEmpty() ? null : p.Address;
        }
        try
        {
            var path = File.Exists(p.Address) ? p.Address : Utils.GetConfigPath(p.Address);
            return File.Exists(path) ? CustomConfigRuleServers.ProxyHost(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Under a white list only relays on Russian IPs can work: the best of them, or null when there is none.</summary>
    public static string? PickNextUnderWhitelist(IEnumerable<Candidate> candidates, string currentId, IEnumerable<string> recentlyTried) =>
        PickNext(candidates.Where(c => c.RussianRelay), currentId, recentlyTried);

    /// <summary>Foreign sites that are open in Russia normally; all of them failing directly while ya.ru opens = white list.</summary>
    private static readonly string[] ForeignProbeUrls = ["https://www.google.com/generate_204", "https://github.com/", "https://www.wikipedia.org/"];

    private static async Task<bool> LooksLikeWhitelistAsync(CancellationToken token)
    {
        var results = await Task.WhenAll(ForeignProbeUrls.Select(u => ZapretWatchdog.ProbeInternetAsync(u, token)));
        return IsWhitelistPattern(russianOpens: true, results);
    }

    public static bool IsWhitelistPattern(bool russianOpens, IReadOnlyCollection<bool> foreignResults) =>
        russianOpens && foreignResults.Count > 0 && foreignResults.All(ok => !ok);

    /// <summary>
    /// Next server: first the same machine over another transport (REALITY → XHTTP → Hysteria2…: a frozen
    /// transport is often the only problem, and the IP stays). Then the same exit country as the current
    /// one (people pick a country for a reason: a service that only works from there), then the rest.
    /// Within that: not on a frozen hosting network, then another network (AS) than the current server's,
    /// so a freeze of one cloud isn't hit again; then ones that answered the last ping (delay &gt; 0) by
    /// delay, untested ones in list order, failed pings (delay &lt; 0) last.
    /// The current and recently tried servers are skipped.
    /// </summary>
    public static string? PickNext(IEnumerable<Candidate> candidates, string currentId, IEnumerable<string> recentlyTried, string? country = null)
    {
        var skip = new HashSet<string>(recentlyTried) { currentId };
        return candidates
            .Where(c => !skip.Contains(c.IndexId))
            .OrderBy(c => c.SameHost ? 0 : 1)
            .ThenBy(c => country != null && c.Country == country ? 0 : 1)
            .ThenBy(c => c.FrozenNetwork ? 1 : 0)
            .ThenBy(c => c.TransportScore < PoorTransport ? 1 : 0)
            .ThenBy(c => c.SameNetwork ? 1 : 0)
            .ThenByDescending(c => c.TransportScore ?? PoorTransport)
            .ThenBy(c => c.Delay > 0 ? 0 : c.Delay == 0 ? 1 : 2)
            .ThenBy(c => c.Delay > 0 ? c.Delay : 0)
            .ThenBy(c => c.Sort)
            .Select(c => c.IndexId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Downloads <see cref="BulkBytes"/> through the local proxy. Fails when no byte arrives for
    /// <see cref="StallTimeout"/> after the response started: what a freeze looks like. A failure to
    /// connect at all counts as alive here (the small probe judges that). Returns the bytes received.
    /// </summary>
    private static async Task<(bool Ok, long Received)> ProbeBulkAsync(CancellationToken token)
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        long received = 0;
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(8),
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(token);
            headers.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await client.GetAsync(BulkProbeUrl, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return (true, 0); // the test site itself refuses: no verdict
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            var buffer = new byte[16 * 1024];
            while (received < BulkBytes)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                stall.CancelAfter(StallTimeout);
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return (false, received); // the response started, then the data stopped
                }
                if (n == 0)
                {
                    break;
                }
                received += n;
            }
            return (true, received);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return (IsFrozen(received, BulkBytes) == false, received);
        }
    }

    /// <summary>The body broke off after some data: a freeze. Nothing at all is a different failure.</summary>
    public static bool IsFrozen(long received, long expected) => received > 0 && received < expected;

    private static async Task<bool> ProbeProxyAsync(CancellationToken token)
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(8),
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var resp = await client.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)resp.StatusCode is >= 200 and < 400)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // retry once, then count as a failure
            }
        }
        return false;
    }
}
