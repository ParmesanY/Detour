namespace ServiceLib.ViewModels;

/// <summary>
/// One clear Connect / Disconnect switch on top of v2rayN's two separate controls (TUN toggle and
/// system proxy mode). "Connected" means system traffic actually goes through the core.
/// </summary>
public partial class ConnectionPanelViewModel : MyReactiveObject
{
    private readonly StatusBarViewModel _statusBar;
    private readonly Timer _engagedPoll;

    public ReactiveCommand<RxVoid, RxVoid> ToggleCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> UnblockCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> CheckIpCmd { get; }

    [Reactive] public partial bool AutoRun { get; set; }
    [Reactive] public partial bool ConnectOnStartup { get; set; }

    /// <summary>Warning about other VPN / bypass apps running; empty if none.</summary>
    [Reactive] public partial string ConflictText { get; set; } = string.Empty;

    [Reactive] public partial string IpCheckText { get; set; } = string.Empty;
    [Reactive] public partial bool IpCheckWarning { get; set; }

    [Reactive] public partial bool KillSwitchEnabled { get; set; }
    [Reactive] public partial bool KillSwitchEngaged { get; set; }
    [Reactive] public partial bool AutoFailover { get; set; }
    [Reactive] public partial bool GameMode { get; set; }
    [Reactive] public partial bool ResumeReconnect { get; set; }

    /// <summary>"Game: cs2.exe — server pinned" while game mode holds the server; empty otherwise.</summary>
    [Reactive] public partial string GameText { get; set; } = string.Empty;

    [Reactive] public partial bool IsConnected { get; set; }

    /// <summary>true: whole computer via TUN; false: only apps that honour the system proxy.</summary>
    [Reactive] public partial bool UseTun { get; set; }

    [Reactive] public partial string StatusTitle { get; set; } = string.Empty;
    [Reactive] public partial string StatusDetail { get; set; } = string.Empty;
    [Reactive] public partial string ButtonText { get; set; } = string.Empty;

    // Live readouts for the connection panel.
    [Reactive] public partial string ServerName { get; set; } = "—";
    [Reactive] public partial string ModeText { get; set; } = "—";
    [Reactive] public partial string UptimeText { get; set; } = "--:--:--";
    [Reactive] public partial string DownText { get; set; } = "—";
    [Reactive] public partial string UpText { get; set; } = "—";
    [Reactive] public partial string DirectText { get; set; } = "—";

    /// <summary>Traffic that went past the VPN today (TrafficSplitManager); empty until there is any.</summary>
    [Reactive] public partial string SavingsText { get; set; } = string.Empty;
    [Reactive] public partial string SavingsDetail { get; set; } = string.Empty;

    /// <summary>Traffic and expiry of the current server's subscription, as the provider reported it.</summary>
    [Reactive] public partial string SubInfoText { get; set; } = string.Empty;
    [Reactive] public partial bool SubInfoWarning { get; set; }

    private async Task RefreshSubInfoAsync()
    {
        try
        {
            var server = _config.IndexId.IsNullOrEmpty() ? null : await AppManager.Instance.GetProfileItem(_config.IndexId);
            var sub = server?.Subid.IsNullOrEmpty() != false ? null : await AppManager.Instance.GetSubItem(server.Subid);
            var (text, warn) = FormatSubInfo(sub?.InfoUsed ?? 0, sub?.InfoTotal ?? 0, sub?.InfoExpire ?? 0, DateTime.Now);
            SubInfoText = text;
            SubInfoWarning = warn;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubInfo", ex);
        }
    }

    /// <summary>"Подписка: 12.3 GB из 100 GB · до 01.12.2026"; warn when ≥90% used or ≤3 days left.</summary>
    public static (string Text, bool Warning) FormatSubInfo(long used, long total, long expire, DateTime now)
    {
        var parts = new List<string>();
        var warn = false;
        if (total > 0)
        {
            parts.Add(string.Format(AppText.SubInfoTraffic, Utils.HumanFy(used), Utils.HumanFy(total)));
            warn |= used >= total * 0.9;
        }
        else if (used > 0)
        {
            parts.Add(string.Format(AppText.SubInfoUsed, Utils.HumanFy(used)));
        }
        if (expire > 0)
        {
            var until = DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime;
            parts.Add(string.Format(AppText.SubInfoExpire, until.ToString("dd.MM.yyyy")));
            warn |= until - now <= TimeSpan.FromDays(3);
        }
        return parts.Count == 0 ? (string.Empty, false) : (AppText.SubInfoTitle + string.Join(" · ", parts), warn);
    }

    private void RefreshSavings()
    {
        var s = TrafficSplitManager.Instance.Snapshot();
        static string Size(long b) => Utils.HumanFy(b);
        static int Percent(long part, long all) => all > 0 ? (int)Math.Round(part * 100.0 / all) : 0;

        var today = s.TodayVpn + s.TodayDirect + s.TodayZapret;
        var total = s.TotalVpn + s.TotalDirect + s.TotalZapret;
        if (total <= 0)
        {
            SavingsText = SavingsDetail = string.Empty;
            return;
        }
        var todayPast = s.TodayDirect + s.TodayZapret;
        var totalPast = s.TotalDirect + s.TotalZapret;
        SavingsText = string.Format(AppText.SavingsToday, Size(todayPast), Size(today), Percent(todayPast, today));
        SavingsDetail = string.Format(AppText.SavingsDetail, Size(s.TodayZapret), Size(s.TodayDirect), Size(s.TodayVpn),
            s.Since?.ToString("dd.MM.yyyy") ?? "—", Size(totalPast), Percent(totalPast, total));
    }

    /// <summary>Last <see cref="HistoryLength" /> per-second samples (bytes/s), newest last, for the graph.</summary>
    [Reactive] public partial double[] DownHistory { get; set; } = new double[HistoryLength];
    [Reactive] public partial double[] UpHistory { get; set; } = new double[HistoryLength];

    public const int HistoryLength = 90;
    private DateTime? _connectedSince;
    private string? _shownIndexId;

    public ConnectionPanelViewModel(StatusBarViewModel statusBar)
    {
        _config = AppManager.Instance.Config;
        _statusBar = statusBar;
        UseTun = _config.UiItem.PreferTunConnect ?? true;

        ToggleCmd = ReactiveCommand.CreateFromTask(ToggleAsync);
        UnblockCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            // Unblocking means the user accepts going without VPN: disconnect too, or it re-engages.
            await ApplyAsync(false);
            await KillSwitchManager.Instance.ReleaseAsync();
            KillSwitchEngaged = false;
        });

        CheckIpCmd = ReactiveCommand.CreateFromTask(CheckIpAsync);

        KillSwitchEnabled = _config.KillSwitchEnabled;
        this.WhenAnyValue(x => x.KillSwitchEnabled).Skip(1).SubscribeAsync(async v =>
        {
            _config.KillSwitchEnabled = v;
            await ConfigHandler.SaveConfig(_config);
        });
        AutoFailover = _config.AutoFailover;
        this.WhenAnyValue(x => x.AutoFailover).Skip(1).SubscribeAsync(async v =>
        {
            _config.AutoFailover = v;
            FailoverManager.Instance.NotifyReloaded();
            await ConfigHandler.SaveConfig(_config);
        });
        GameMode = _config.GameMode != false;
        this.WhenAnyValue(x => x.GameMode).Skip(1).SubscribeAsync(async v =>
        {
            _config.GameMode = v;
            await ConfigHandler.SaveConfig(_config);
        });
        ResumeReconnect = _config.ResumeReconnect != false;
        this.WhenAnyValue(x => x.ResumeReconnect).Skip(1).SubscribeAsync(async v =>
        {
            _config.ResumeReconnect = v;
            await ConfigHandler.SaveConfig(_config);
        });
        GameModeManager.Instance.StateChanged += game =>
            RxSchedulers.MainThreadScheduler.Schedule(() =>
                GameText = game != null && _config.GameMode != false ? string.Format(AppText.GameModeActive, game) : string.Empty);
        var ticks = 0;
        _engagedPoll = new Timer(_ =>
        {
            KillSwitchEngaged = KillSwitchManager.Instance.IsEngaged;
            UptimeText = _connectedSince is { } since ? FormatUptime(DateTime.Now - since) : "--:--:--";
            // The selected server can change without any status bar event reaching us.
            if (_config.IndexId != _shownIndexId)
            {
                _shownIndexId = _config.IndexId;
                Refresh();
            }
            if (ticks % 5 == 0)
            {
                RefreshSavings();
            }
            if (ticks % 30 == 0)
            {
                _ = RefreshSubInfoAsync();
            }
            // Other VPN apps: checked every ~10 s (process scan is not free).
            if (ticks++ % 10 == 0)
            {
                RefreshConflicts();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnStatistics);

        AutoRun = _config.GuiItem.AutoRun;
        this.WhenAnyValue(x => x.AutoRun).Skip(1).SubscribeAsync(async v =>
        {
            _config.GuiItem.AutoRun = v;
            await ConfigHandler.SaveConfig(_config);
            await AutoStartupHandler.UpdateTask(_config);
        });
        ConnectOnStartup = _config.UiItem.ConnectOnStartup ?? true;
        this.WhenAnyValue(x => x.ConnectOnStartup).Skip(1).SubscribeAsync(async v =>
        {
            _config.UiItem.ConnectOnStartup = v;
            await ConfigHandler.SaveConfig(_config);
        });

        _statusBar.WhenAnyValue(x => x.EnableTun, x => x.SystemProxySelected, x => x.RunningServerDisplay, x => x.SelectedServer)
            .Subscribe(_ => Refresh());
        this.WhenAnyValue(x => x.UseTun).Skip(1).SubscribeAsync(async v =>
        {
            _config.UiItem.PreferTunConnect = v;
            await ConfigHandler.SaveConfig(_config);
            if (IsConnected)
            {
                // Switch the running connection to the other mode.
                await ApplyAsync(true);
            }
        });
        Refresh();
    }

    private static bool ProxyOn(int selected) =>
        selected is (int)ESysProxyType.ForcedChange or (int)ESysProxyType.Pac;

    /// <summary>
    /// The active server comes from the config, not from the status bar's server list: that list is
    /// left empty when there are more servers than fit in the tray menu.
    /// </summary>
    private string? _serverName;

    private async Task RefreshServerNameAsync()
    {
        var id = _config.IndexId;
        var profile = id.IsNullOrEmpty() ? null : await AppManager.Instance.GetProfileItem(id);
        _serverName = profile == null ? null : ServerNameFormatter.Split(profile.Remarks).Name;
    }

    private void Refresh()
    {
        _ = RefreshAndShowAsync();
    }

    private async Task RefreshAndShowAsync()
    {
        await RefreshServerNameAsync();
        Show();
    }

    private void Show()
    {
        IsConnected = _statusBar.EnableTun || ProxyOn(_statusBar.SystemProxySelected);
        var server = _serverName;
        var noServer = server.IsNullOrEmpty();

        if (IsConnected)
        {
            _connectedSince ??= DateTime.Now;
        }
        else
        {
            _connectedSince = null;
            DownText = UpText = DirectText = "—";
        }
        ServerName = noServer ? "—" : server!;
        ModeText = !IsConnected ? AppText.ConnModeOff : _statusBar.EnableTun ? "TUN" : "PROXY";

        StatusTitle = IsConnected ? AppText.ConnConnected : AppText.ConnDisconnected;
        StatusDetail = noServer
            ? AppText.ConnNoServer
            : IsConnected
                ? string.Format(_statusBar.EnableTun ? AppText.ConnViaTun : AppText.ConnViaProxy, server)
                : string.Format(AppText.ConnSelected, server);
        ButtonText = (IsConnected ? AppText.ConnDisconnect : AppText.ConnConnect).ToUpperInvariant();
    }

    /// <summary>
    /// Asks a public "what is my IP" service the way ordinary apps would (TUN or system proxy) and
    /// warns if the visible address is Russian, i.e. traffic probably bypasses the VPN.
    /// </summary>
    private async Task CheckIpAsync()
    {
        IpCheckWarning = false;
        IpCheckText = AppText.IpChecking;
        try
        {
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.GetSystemWebProxy() };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var json = JsonUtils.ParseJson(await client.GetStringAsync("https://ipwho.is/"));
            var ip = json?["ip"]?.ToString();
            var country = json?["country"]?.ToString();
            var code = json?["country_code"]?.ToString();
            var isp = json?["connection"]?["isp"]?.ToString();
            if (ip.IsNullOrEmpty())
            {
                IpCheckText = AppText.IpCheckFailed;
                return;
            }
            IpCheckWarning = string.Equals(code, "RU", StringComparison.OrdinalIgnoreCase);
            IpCheckText = string.Format(IpCheckWarning ? AppText.IpCheckRussian : AppText.IpCheckResult, ip, country, isp);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CheckIp", ex);
            IpCheckText = AppText.IpCheckFailed;
        }
    }

    private void OnStatistics(ServerSpeedItem update)
    {
        if (!IsConnected)
        {
            return;
        }
        DownText = FormatSpeed(update.ProxyDown);
        UpText = FormatSpeed(update.ProxyUp);
        DirectText = FormatSpeed(update.DirectDown + update.DirectUp);
        DownHistory = Push(DownHistory, update.ProxyDown + update.DirectDown);
        UpHistory = Push(UpHistory, update.ProxyUp + update.DirectUp);

        static double[] Push(double[] history, long value)
        {
            var next = new double[HistoryLength];
            Array.Copy(history, 1, next, 0, HistoryLength - 1);
            next[^1] = Math.Max(0, value);
            return next;
        }
    }

    /// <summary>"12.4 MB/s", "512 KB/s", "0 B/s".</summary>
    public static string FormatSpeed(long bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double v = Math.Max(0, bytesPerSecond);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{v:0} {units[i]}" : v < 10 ? $"{v:0.0} {units[i]}" : $"{v:0} {units[i]}";
    }

    public static string FormatUptime(TimeSpan t)
    {
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void RefreshConflicts()
    {
        try
        {
            var conflicts = VpnConflictDetector.Detect();
            ConflictText = conflicts.Count == 0 ? string.Empty : string.Format(AppText.ConflictWarning, string.Join(", ", conflicts));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("VpnConflictDetector", ex);
        }
    }

    private async Task ToggleAsync()
    {
        RefreshConflicts();
        await RefreshServerNameAsync();
        if (!IsConnected && _serverName.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(AppText.ConnNoServer);
            return;
        }
        await ApplyAsync(!IsConnected);
    }

    private async Task ApplyAsync(bool connect)
    {
        if (!connect)
        {
            _statusBar.EnableTun = false;
            _statusBar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
        }
        else if (UseTun)
        {
            // TUN covers every app; the system proxy would be redundant.
            _statusBar.SystemProxySelected = (int)ESysProxyType.ForcedClear;
            _statusBar.EnableTun = true;
        }
        else
        {
            _statusBar.EnableTun = false;
            _statusBar.SystemProxySelected = (int)ESysProxyType.ForcedChange;
        }
        await RefreshAndShowAsync();
    }
}
