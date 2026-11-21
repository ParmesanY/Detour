namespace ServiceLib.Manager;

/// <summary>
/// Long connection watchdog. The ISP's freeze stops a long TCP tunnel without closing it: Discord voice
/// jumps to a 5000 ms ping, a call or a game hangs until the user reconnects. With sing-box running
/// (TUN or the sing-box core) its Clash API lists every connection with byte counters; a connection
/// through the VPN whose app keeps sending small amounts while nothing has come back for
/// <see cref="StallAfter"/> is closed, so the app opens a new one at once. A bulk upload (lots of
/// bytes out, nothing expected back) doesn't count.
/// </summary>
public sealed class StallWatchManager
{
    private static readonly Lazy<StallWatchManager> _instance = new(() => new());
    public static StallWatchManager Instance => _instance.Value;
    private const string _tag = "StallWatch";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(20);
    /// <summary>The app must have sent something this recently: an idle connection isn't stalled.</summary>
    public static readonly TimeSpan RecentUpload = TimeSpan.FromSeconds(10);
    public const long MinDownloadBefore = 2048;
    public const long MaxUploadWhileStalled = 16 * 1024;
    public const int MaxClosesPerMinute = 5;
    private static readonly TimeSpan NoticeEvery = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> NotThroughVpn = new(StringComparer.OrdinalIgnoreCase)
    {
        Global.DirectTag, Global.BlockTag, "dns-out", "dns",
    };

    private CancellationTokenSource? _cts;
    private readonly Detector _detector = new();

    /// <summary>Every poll's connections (worker thread), for other watchers (SiteAutoFixManager): one poll for all.</summary>
    public event Action<DateTime, IReadOnlyList<ConnectionItem>>? ConnectionsPolled;
    private DateTime _lastNotice = DateTime.MinValue;

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

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, token);
                if (!AppManager.Instance.IsRunningCore(ECoreType.sing_box))
                {
                    _detector.Reset();
                    continue;
                }
                var connections = (await ClashApiManager.Instance.GetConnections())?.connections;
                if (connections == null)
                {
                    continue;
                }
                var now = DateTime.Now;
                try
                {
                    ConnectionsPolled?.Invoke(now, connections);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
                foreach (var c in _detector.Observe(now, connections.Select(ToSample)))
                {
                    var what = c.Host.IsNullOrEmpty() ? c.Id : c.Host;
                    Logging.SaveLog($"{_tag}: {what} ({c.Process}) gets nothing back for {StallAfter.TotalSeconds:0} s, closing it");
                    await ClashApiManager.Instance.CloseConnection(c.Id);
                    if (now - _lastNotice > NoticeEvery)
                    {
                        _lastNotice = now;
                        NoticeManager.Instance.Enqueue(string.Format(AppText.StallWatchClosed, what));
                    }
                }
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

    private static Sample ToSample(ConnectionItem c)
    {
        var chains = c.chains ?? [];
        var host = c.metadata?.host.IsNullOrEmpty() == false ? c.metadata.host : c.metadata?.destinationIP;
        return new Sample(c.id ?? "", (long)c.upload, (long)c.download,
            chains.Count > 0 && !chains.Any(NotThroughVpn.Contains),
            host, Path.GetFileName(c.metadata?.processPath ?? c.metadata?.process ?? ""));
    }

    public record Sample(string Id, long Upload, long Download, bool ThroughVpn, string? Host = null, string? Process = null);

    /// <summary>Byte-counter history per connection; pure, for tests.</summary>
    public sealed class Detector
    {
        private sealed class Track
        {
            public long Up, Down, UpAtDownChange;
            public DateTime DownChangedAt, UpChangedAt;
        }

        private readonly Dictionary<string, Track> _tracks = new();
        private readonly List<DateTime> _closes = [];

        public void Reset()
        {
            _tracks.Clear();
        }

        /// <summary>Takes the current connections; returns the ones to close now.</summary>
        public List<Sample> Observe(DateTime now, IEnumerable<Sample> samples)
        {
            var result = new List<Sample>();
            var seen = new HashSet<string>();
            _closes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            foreach (var s in samples)
            {
                if (s.Id.Length == 0 || !s.ThroughVpn)
                {
                    continue;
                }
                seen.Add(s.Id);
                if (!_tracks.TryGetValue(s.Id, out var t))
                {
                    _tracks[s.Id] = new Track { Up = s.Upload, Down = s.Download, UpAtDownChange = s.Upload, DownChangedAt = now, UpChangedAt = now };
                    continue;
                }
                if (s.Download != t.Down)
                {
                    t.Down = s.Download;
                    t.DownChangedAt = now;
                    t.UpAtDownChange = s.Upload;
                }
                if (s.Upload != t.Up)
                {
                    t.Up = s.Upload;
                    t.UpChangedAt = now;
                }

                var sentSince = t.Up - t.UpAtDownChange;
                if (t.Down >= MinDownloadBefore
                    && now - t.DownChangedAt >= StallAfter
                    && now - t.UpChangedAt <= RecentUpload
                    && sentSince is > 0 and <= MaxUploadWhileStalled
                    && _closes.Count < MaxClosesPerMinute)
                {
                    _closes.Add(now);
                    _tracks.Remove(s.Id);
                    result.Add(s);
                }
            }
            foreach (var gone in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _tracks.Remove(gone);
            }
            return result;
        }
    }
}
