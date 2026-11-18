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

        if (await ProbeProxyAsync(token))
        {
            _failures = 0;
            _heldNotified = false;
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
        var candidates = group.Select(p => new Candidate(p.IndexId,
                exs.TryGetValue(p.IndexId, out var ex) ? ex.Delay : 0,
                exs.TryGetValue(p.IndexId, out var ex2) ? ex2.Sort : 0,
                ServerNameFormatter.ExitCountry(p.Remarks)))
            .ToList();
        var next = PickNext(candidates, config.IndexId, _tried.Keys, ServerNameFormatter.ExitCountry(current?.Remarks));
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

    public record Candidate(string IndexId, int Delay, int Sort, string? Country = null);

    /// <summary>
    /// Next server: same exit country as the current one first (people pick a country for a reason:
    /// a service that only works from there), then the rest. Within that: ones that answered the last
    /// ping (delay &gt; 0) by delay, then untested ones in list order; failed pings (delay &lt; 0) last.
    /// The current and recently tried servers are skipped.
    /// </summary>
    public static string? PickNext(IEnumerable<Candidate> candidates, string currentId, IEnumerable<string> recentlyTried, string? country = null)
    {
        var skip = new HashSet<string>(recentlyTried) { currentId };
        return candidates
            .Where(c => !skip.Contains(c.IndexId))
            .OrderBy(c => country != null && c.Country == country ? 0 : 1)
            .ThenBy(c => c.Delay > 0 ? 0 : c.Delay == 0 ? 1 : 2)
            .ThenBy(c => c.Delay > 0 ? c.Delay : 0)
            .ThenBy(c => c.Sort)
            .Select(c => c.IndexId)
            .FirstOrDefault();
    }

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
