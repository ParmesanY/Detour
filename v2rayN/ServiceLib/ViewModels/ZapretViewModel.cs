namespace ServiceLib.ViewModels;

public partial class ZapretViewModel : MyReactiveObject
{
    public ReactiveCommand<RxVoid, RxVoid> InstallCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StartCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StopCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AutoSelectCmd { get; }

    public BulkObservableCollection<string> Strategies { get; } = [];

    public List<KeyValuePair<string, string>> GameFilterModes { get; } =
    [
        new("disabled", AppText.ZapretGameOff),
        new("all", AppText.ZapretGameAll),
        new("tcp", "TCP"),
        new("udp", "UDP"),
    ];

    /// <summary>Windows engine (winws): game filter and Flowseal strategies apply.</summary>
    public bool IsWinws => !ZapretManager.UsesTpws;

    public string SourceText => ZapretManager.UsesTpws
        ? "github.com/bol-van/zapret (tpws, MIT)"
        : "github.com/Flowseal/zapret-discord-youtube (MIT)";

    [Reactive] public partial bool IsInstalled { get; set; }
    [Reactive] public partial bool IsRunning { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }
    [Reactive] public partial bool IsAdmin { get; set; }
    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial string? SelectedStrategy { get; set; }
    [Reactive] public partial string GameFilterMode { get; set; } = ZapretStrategyParser.GameFilterDisabled;
    [Reactive] public partial string VersionText { get; set; } = string.Empty;
    [Reactive] public partial string StateText { get; set; } = string.Empty;
    [Reactive] public partial string ProgressText { get; set; } = string.Empty;

    private bool _loading;

    public ZapretViewModel()
    {
        _config = AppManager.Instance.Config;
        var notBusy = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canRun = this.WhenAnyValue(x => x.IsBusy, x => x.IsInstalled, x => x.IsAdmin, (b, i, a) => !b && i && a);

        InstallCmd = ReactiveCommand.CreateFromTask(InstallAsync, notBusy);
        StartCmd = ReactiveCommand.CreateFromTask(StartAsync, canRun);
        StopCmd = ReactiveCommand.CreateFromTask(StopAsync, notBusy);
        AutoSelectCmd = ReactiveCommand.CreateFromTask(AutoSelectAsync, canRun);

        Refresh();

        // winws is usually started at app start, after this page was built; follow its real state.
        // Full refresh: the first-run wizard can switch zapret on and pick a strategy from outside this page.
        ZapretManager.Instance.StateChanged += () => RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (!IsBusy)
            {
                Refresh();
            }
            else
            {
                RefreshState();
            }
        });

        this.WhenAnyValue(x => x.Enabled).Skip(1).SubscribeAsync(async v =>
        {
            if (_loading)
            {
                return;
            }
            _config.ZapretItem.Enabled = v;
            await ConfigHandler.SaveConfig(_config);
            var msg = await ConnectionRulesManager.Instance.SyncZapretAsync(_config);
            ProgressText = msg ?? string.Empty;
            RefreshState();
            // "zapret then VPN" rules route differently with zapret on or off.
            AppEvents.CoreReloadRequested.Publish();
        });
        this.WhenAnyValue(x => x.SelectedStrategy).Skip(1).Where(s => s.IsNotEmpty()).SubscribeAsync(async s =>
        {
            if (_loading || s == _config.ZapretItem.Strategy)
            {
                return;
            }
            _config.ZapretItem.Strategy = s!;
            // Picking a .bat by hand drops a registry strategy laid over the previous one.
            _config.ZapretItem.Desync = null;
            _config.ZapretItem.DesyncNote = null;
            // ...and the core's "TLS records" mode: the sites go back to winws.
            var hadRecords = _config.ZapretItem.SiteRecords.Length > 0;
            _config.ZapretItem.SiteRecords = string.Empty;
            RegistryNote = string.Empty;
            await ConfigHandler.SaveConfig(_config);
            // A hand pick is this network's strategy from now on (netsh is slow: off the UI thread).
            _ = Task.Run(() => NetworkProfiles.RememberCurrent(_config.ZapretItem));
            await RestartIfRunning();
            if (hadRecords)
            {
                await ConnectionRulesManager.Instance.SyncZapretAsync(_config);
                AppEvents.CoreReloadRequested.Publish();
            }
        });
        this.WhenAnyValue(x => x.GameFilterMode).Skip(1).Where(s => s.IsNotEmpty()).SubscribeAsync(async m =>
        {
            if (_loading || m == _config.ZapretItem.GameFilterMode)
            {
                return;
            }
            _config.ZapretItem.GameFilterMode = m;
            await ConfigHandler.SaveConfig(_config);
            await RestartIfRunning();
        });
        AutoHeal = _config.ZapretItem.AutoHeal;
        this.WhenAnyValue(x => x.AutoHeal).Skip(1).SubscribeAsync(async v =>
        {
            _config.ZapretItem.AutoHeal = v;
            await ConfigHandler.SaveConfig(_config);
        });
        RegistryHints = _config.ZapretItem.RegistryHints;
        this.WhenAnyValue(x => x.RegistryHints).Skip(1).SubscribeAsync(async v =>
        {
            _config.ZapretItem.RegistryHints = v;
            await ConfigHandler.SaveConfig(_config);
        });
        InitVpnStrategy();
    }

    [Reactive] public partial bool AutoHeal { get; set; }

    [Reactive] public partial bool RegistryHints { get; set; }

    /// <summary>"from the Detour Map registry: …" while a registry strategy rides on the .bat, else empty.</summary>
    [Reactive] public partial string RegistryNote { get; set; } = string.Empty;

    private void Refresh()
    {
        _loading = true;
        try
        {
            var item = _config.ZapretItem;
            // tpws (macOS/Linux) runs as a plain local proxy, no admin rights needed.
            IsAdmin = ZapretManager.UsesTpws || Utils.IsAdministrator();
            IsInstalled = ZapretManager.Instance.IsInstalled;
            Strategies.Clear();
            Strategies.AddRange(ZapretManager.Instance.GetStrategies());
            SelectedStrategy = item.Strategy;
            RegistryNote = item.Desync.IsNullOrEmpty() ? string.Empty : "+ " + (item.DesyncNote ?? string.Empty);
            GameFilterMode = item.GameFilterMode;
            Enabled = item.Enabled;
            VersionText = IsInstalled ? string.Format(AppText.ZapretInstalled, item.InstalledVersion ?? ZapretManager.BundledVersion ?? "?") : AppText.ZapretNotInstalled;
            RefreshState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        IsRunning = ZapretManager.Instance.IsRunning;
        StateText = IsRunning ? AppText.ZapretRunning : AppText.ZapretStopped;
    }

    private async Task RestartIfRunning()
    {
        if (!ZapretManager.Instance.IsRunning)
        {
            return;
        }
        await StartAsync();
    }

    private async Task InstallAsync()
    {
        IsBusy = true;
        try
        {
            using var downloadService = DownloadService.CreateDownloadServiceWithFallback();
            var (_, message) = await ZapretManager.Instance.InstallAsync(downloadService, null, m =>
            {
                ProgressText = m;
                return Task.CompletedTask;
            });
            ProgressText = message;
            Refresh();
            if (IsInstalled)
            {
                await ConnectionRulesManager.Instance.SyncZapretAsync(_config);
                RefreshState();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            // Lists must match the rules before winws reads them.
            var compiled = ConnectionRulesManager.Instance.Compile(_config);
            ZapretManager.WriteUserLists(compiled.ZapretDomains, compiled.ZapretIps, compiled.ZapretExcludeDomains,
                await ZapretManager.GetVpnServerIpsAsync());
            var (_, message) = await ZapretManager.Instance.StartAsync();
            ProgressText = message;
            RefreshState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            await ZapretManager.Instance.StopAsync();
            ProgressText = string.Empty;
            RefreshState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AutoSelectAsync()
    {
        IsBusy = true;
        try
        {
            var (best, all) = await ZapretManager.Instance.AutoSelectStrategyAsync(_config, true, m =>
            {
                ProgressText = m;
                return Task.CompletedTask;
            });
            ProgressText = best is { Passed: > 0 }
                ? string.Format(AppText.ZapretAutoResult, ZapretManager.DisplayStrategy(_config.ZapretItem), best.Passed, best.Total)
                : all.Count == 0 ? AppText.ZapretSelectBusy : AppText.ZapretAutoNothing;
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
