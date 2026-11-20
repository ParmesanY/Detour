namespace ServiceLib.ViewModels;

public partial class TgWsProxyViewModel : MyReactiveObject
{
    public ReactiveCommand<RxVoid, RxVoid> InstallCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StartCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StopCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ConnectTelegramCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> NewSecretCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> UseStandaloneSecretCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ApplyPortCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenLogCmd { get; }

    [Reactive] public partial bool IsInstalled { get; set; }
    [Reactive] public partial bool IsRunning { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }
    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial string PortText { get; set; } = string.Empty;
    [Reactive] public partial string Link { get; set; } = string.Empty;

    /// <summary>The user's own TG WS Proxy uses another secret: clients added to it get "wrong secret" from ours.</summary>
    [Reactive] public partial bool StandaloneSecretDiffers { get; set; }
    [Reactive] public partial string VersionText { get; set; } = string.Empty;
    [Reactive] public partial string StateText { get; set; } = string.Empty;
    [Reactive] public partial string ProgressText { get; set; } = string.Empty;

    private bool _loading;

    public TgWsProxyViewModel()
    {
        _config = AppManager.Instance.Config;
        var notBusy = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canRun = this.WhenAnyValue(x => x.IsBusy, x => x.IsInstalled, (b, i) => !b && i);

        InstallCmd = ReactiveCommand.CreateFromTask(InstallAsync, notBusy);
        StartCmd = ReactiveCommand.CreateFromTask(StartAsync, canRun);
        StopCmd = ReactiveCommand.CreateFromTask(StopAsync, notBusy);
        ConnectTelegramCmd = ReactiveCommand.CreateFromTask(ConnectTelegramAsync, notBusy);
        NewSecretCmd = ReactiveCommand.CreateFromTask(NewSecretAsync, notBusy);
        UseStandaloneSecretCmd = ReactiveCommand.CreateFromTask(UseStandaloneSecretAsync, notBusy);
        ApplyPortCmd = ReactiveCommand.CreateFromTask(ApplyPortAsync, notBusy);
        OpenLogCmd = ReactiveCommand.Create(() =>
        {
            if (File.Exists(TgWsProxyManager.LogPath))
            {
                ProcUtils.ProcessStart(TgWsProxyManager.LogPath);
            }
        });

        Refresh();

        // The proxy is usually started at app start, after this page was built; follow its real state.
        TgWsProxyManager.Instance.StateChanged += () => RxSchedulers.MainThreadScheduler.Schedule(RefreshState);

        this.WhenAnyValue(x => x.Enabled).Skip(1).SubscribeAsync(async v =>
        {
            if (_loading)
            {
                return;
            }
            _config.TgWsProxyItem.Enabled = v;
            await ConfigHandler.SaveConfig(_config);
            if (v && IsInstalled && !TgWsProxyManager.Instance.IsRunning)
            {
                await StartAsync();
            }
            else if (!v && TgWsProxyManager.Instance.IsRunning)
            {
                await StopAsync();
            }
            // The proxy's direct-route rule comes and goes with this switch.
            AppEvents.CoreReloadRequested.Publish();
        });
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            var item = _config.TgWsProxyItem;
            IsInstalled = TgWsProxyManager.Instance.IsInstalled;
            Enabled = item.Enabled;
            PortText = item.Port.ToString();
            Link = TgWsProxyManager.GetLink(_config) ?? string.Empty;
            var standalone = TgWsProxyManager.StandaloneSecret(item.Port);
            StandaloneSecretDiffers = standalone != null && TgWsProxyManager.IsValidSecret(item.Secret)
                                      && !standalone.Equals(item.Secret, StringComparison.OrdinalIgnoreCase);
            VersionText = IsInstalled ? string.Format(AppText.TgInstalled, item.InstalledVersion ?? "?") : AppText.TgNotInstalled;
            RefreshState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        IsRunning = TgWsProxyManager.Instance.IsRunning;
        StateText = IsRunning ? AppText.TgRunning : AppText.TgStopped;
    }

    private async Task RunBusy(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task InstallAsync() => RunBusy(async () =>
    {
        using var downloadService = DownloadService.CreateDownloadServiceWithFallback();
        var (ok, message) = await TgWsProxyManager.Instance.InstallAsync(downloadService, null, m =>
        {
            ProgressText = m;
            return Task.CompletedTask;
        });
        ProgressText = message;
        Refresh();
        if (ok && _config.TgWsProxyItem.Enabled && !TgWsProxyManager.Instance.IsRunning)
        {
            var (_, msg) = await TgWsProxyManager.Instance.StartAsync();
            ProgressText = msg;
            Refresh();
        }
    });

    private Task StartAsync() => RunBusy(async () =>
    {
        var (_, message) = await TgWsProxyManager.Instance.StartAsync();
        ProgressText = message;
        Refresh();
    });

    private Task StopAsync() => RunBusy(async () =>
    {
        await TgWsProxyManager.Instance.StopAsync();
        ProgressText = string.Empty;
        RefreshState();
    });

    private Task ConnectTelegramAsync() => RunBusy(async () =>
    {
        if (!TgWsProxyManager.Instance.IsRunning && IsInstalled)
        {
            var (ok, msg) = await TgWsProxyManager.Instance.StartAsync();
            if (!ok)
            {
                ProgressText = msg;
                Refresh();
                return;
            }
        }
        await TgWsProxyManager.EnsureSecretAsync(_config);
        Refresh();
        TgWsProxyManager.OpenInTelegram(_config);
    });

    private Task NewSecretAsync() => RunBusy(async () =>
    {
        _config.TgWsProxyItem.Secret = TgWsProxyManager.NewSecret();
        await ConfigHandler.SaveConfig(_config);
        await RestartIfRunning();
        Refresh();
    });

    private Task UseStandaloneSecretAsync() => RunBusy(async () =>
    {
        if (TgWsProxyManager.StandaloneSecret(_config.TgWsProxyItem.Port) is not { } secret)
        {
            return;
        }
        _config.TgWsProxyItem.Secret = secret;
        await ConfigHandler.SaveConfig(_config);
        await RestartIfRunning();
        Refresh();
    });

    private Task ApplyPortAsync() => RunBusy(async () =>
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1024 or > 65535)
        {
            ProgressText = "1024–65535";
            return;
        }
        if (port == _config.TgWsProxyItem.Port)
        {
            return;
        }
        _config.TgWsProxyItem.Port = port;
        await ConfigHandler.SaveConfig(_config);
        await RestartIfRunning();
        Refresh();
    });

    private async Task RestartIfRunning()
    {
        if (!TgWsProxyManager.Instance.IsRunning)
        {
            return;
        }
        var (_, msg) = await TgWsProxyManager.Instance.StartAsync();
        ProgressText = msg;
    }
}
