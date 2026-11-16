using Detour.NetProbe;

namespace ServiceLib.Manager;

/// <summary>Outcome of one Detour Map measurement.</summary>
/// <param name="Report">What was measured (null when nothing ran).</param>
/// <param name="Sent">The collector's answer (null when nothing was sent).</param>
/// <param name="Message">One line for the user.</param>
public sealed record BlockMapRun(ProbeReport? Report, SendResult? Sent, string Message);

/// <summary>
/// Detour Map (external/detour-map): measures what the ISP blocks and how, with the same code as the detour-probe
/// CLI, and sends it to the public map when the user opted in. Everything goes over the physical network card, so
/// the VPN doesn't hide the ISP; the report carries no IP address, and the collector keeps none.
/// zapret makes blocked sites look open, so the background job skips while it runs, and a manual run pauses it.
/// </summary>
public sealed class BlockMapManager
{
    private const string _tag = "BlockMap";

    public static BlockMapManager Instance { get; } = new();

    /// <summary>The collector and the map site (same origin).</summary>
    public const string Endpoint = "https://detour-map-collector.detour-map-collector.workers.dev";

    /// <summary>Background measurements at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>The first background check waits for the VPN and zapret to settle after start.</summary>
    private static readonly TimeSpan _startDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan _tick = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _started;

    /// <summary>Last measurement of this session, for "show what is sent".</summary>
    public BlockMapRun? LastRun { get; private set; }

    public bool IsBusy => _gate.CurrentCount == 0;

    public event Action? StateChanged;

    public static string Client => $"detour/{AppUpdateManager.CurrentVersion}";

    /// <summary>Is a background measurement due? Never while zapret or another DPI bypass runs.</summary>
    public static bool IsDue(BlockMapItem item, DateTime nowUtc, bool bypassRunning)
    {
        return item.Enabled && !bypassRunning && (item.LastAttempt is not { } last || nowUtc - last >= Interval);
    }

    public void Start(Config config)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        Task.Run(async () =>
        {
            await Task.Delay(_startDelay).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    var item = config.BlockMapItem;
                    if (item is not null && !IsBusy
                        && IsDue(item, DateTime.UtcNow, ZapretManager.Instance.IsRunning || Prober.DpiBypassRunning()))
                    {
                        var run = await RunAsync(config, send: true, pauseZapret: false).ConfigureAwait(false);
                        Logging.SaveLog($"{_tag}: {run.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
                await Task.Delay(_tick).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Measures (1–2 minutes) and, with <paramref name="send" />, sends the report. With
    /// <paramref name="pauseZapret" /> a running zapret is stopped for the measurement and started again after it;
    /// without it a running zapret cancels the run.
    /// </summary>
    public async Task<BlockMapRun> RunAsync(Config config, bool send, bool pauseZapret,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return new BlockMapRun(null, null, AppText.BlockMapBusy);
        }
        var item = config.BlockMapItem ??= new BlockMapItem();
        var zapret = ZapretManager.Instance;
        var zapretWasRunning = zapret.IsRunning;
        try
        {
            StateChanged?.Invoke();
            if (zapretWasRunning && !pauseZapret)
            {
                return Remember(new BlockMapRun(null, null, AppText.BlockMapZapretRunning));
            }
            if (!zapretWasRunning && Prober.DpiBypassRunning())
            {
                return Remember(new BlockMapRun(null, null, AppText.BlockMapOtherBypass));
            }
            if (zapretWasRunning)
            {
                progress?.Report(AppText.BlockMapPausingZapret);
                await zapret.StopAsync();
            }

            item.LastAttempt = DateTime.UtcNow;
            var nic = NicInfo.Detect();
            var total = TargetList.Default.Targets.Count;
            var done = 0;
            var report = await Prober.RunAsync(new ProbeOptions
            {
                Client = Client,
                Net = item.Net,
                Nic = nic,
                Progress = new Progress<TargetResult>(r =>
                    progress?.Report(string.Format(AppText.BlockMapProgress, Interlocked.Increment(ref done), total, r.T))),
            }, ct);

            // Something (zapret's own restart, another tool) started a bypass mid-run: the results can't be trusted.
            if (report.Env.DpiBypass || Prober.DpiBypassRunning())
            {
                return Remember(new BlockMapRun(report, null, AppText.BlockMapBypassDuringRun));
            }
            if (!send)
            {
                return Remember(new BlockMapRun(report, null, AppText.BlockMapMeasured));
            }

            progress?.Report(AppText.BlockMapSending);
            SendResult sent;
            try
            {
                sent = await ReportSender.SendAsync(report, new Uri(Endpoint), nic, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                return Remember(new BlockMapRun(report, null, string.Format(AppText.BlockMapSendFailed, ex.Message)));
            }
            if (sent.Accepted)
            {
                item.LastSent = DateTime.UtcNow;
            }
            return Remember(new BlockMapRun(report, sent, sent.Accepted
                ? AppText.BlockMapSent
                : string.Format(AppText.BlockMapRefused, sent.HttpStatus, sent.Error)));
        }
        finally
        {
            if (zapretWasRunning && !zapret.IsRunning)
            {
                progress?.Report(AppText.BlockMapResumingZapret);
                var error = await ConnectionRulesManager.Instance.SyncZapretAsync(config);
                if (error != null)
                {
                    Logging.SaveLog($"{_tag}: zapret restart: {error}");
                }
            }
            await ConfigHandler.SaveConfig(config);
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    private BlockMapRun Remember(BlockMapRun run)
    {
        LastRun = run;
        return run;
    }

    /// <summary>The run as text: a table with verdicts, then the exact JSON that is (or would be) sent.</summary>
    public static string Describe(BlockMapRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine(run.Message);
        if (run.Report is not { } report)
        {
            return sb.ToString();
        }
        var names = TargetList.Default.Targets.ToDictionary(t => t.Id, t => t.Name);
        sb.AppendLine();
        sb.AppendLine($"{AppText.BlockMapColTarget,-20} {"DNS",-8} {"TCP",-8} {"TLS",-14} {AppText.BlockMapColVerdict}");
        sb.AppendLine(new string('-', 72));
        foreach (var r in report.Results)
        {
            var tls = r.Tls is null ? "" : r.TlsCtrl is null ? r.Tls : $"{r.Tls}/ctl {r.TlsCtrl}";
            var verdicts = run.Sent?.Verdicts?.GetValueOrDefault(r.T) ?? Classifier.Classify(r);
            sb.AppendLine($"{names.GetValueOrDefault(r.T, r.T),-20} {r.Dns,-8} {r.Tcp,-8} {tls,-14} {string.Join(", ", verdicts)}");
        }
        sb.AppendLine();
        sb.AppendLine(AppText.BlockMapJsonHeader);
        sb.AppendLine(JsonSerializer.Serialize(report,
            new ProbeJson(new JsonSerializerOptions(ProbeJson.Default.Options) { WriteIndented = true }).ProbeReport));
        return sb.ToString();
    }
}
