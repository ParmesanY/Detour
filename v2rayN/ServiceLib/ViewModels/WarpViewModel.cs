namespace ServiceLib.ViewModels;

/// <summary>The WARP page: device registration and the local WARP proxy that "+warp" rules use.</summary>
public partial class WarpViewModel : MyReactiveObject
{
    public ReactiveCommand<RxVoid, RxVoid> RegisterCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StartCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> StopCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> TestCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ApplyPortCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ApplyEndpointCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenLogCmd { get; }

    public IReadOnlyList<string> Endpoints { get; } = WarpManager.Endpoints;

    [Reactive] public partial bool IsRegistered { get; set; }
    [Reactive] public partial bool IsRunning { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }
    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial bool Noise { get; set; }
    [Reactive] public partial string PortText { get; set; } = string.Empty;
    [Reactive] public partial string EndpointText { get; set; } = string.Empty;
    [Reactive] public partial string StateText { get; set; } = string.Empty;
    [Reactive] public partial string DeviceText { get; set; } = string.Empty;
    [Reactive] public partial string ProgressText { get; set; } = string.Empty;

    private bool _loading;

    public WarpViewModel()
    {
        _config = AppManager.Instance.Config;
        var notBusy = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canRun = this.WhenAnyValue(x => x.IsBusy, x => x.IsRegistered, (b, r) => !b && r);

        RegisterCmd = ReactiveCommand.CreateFromTask(RegisterAsync, notBusy);
        StartCmd = ReactiveCommand.CreateFromTask(StartAsync, canRun);
        StopCmd = ReactiveCommand.CreateFromTask(StopAsync, notBusy);
        TestCmd = ReactiveCommand.CreateFromTask(TestAsync, canRun);
        ApplyPortCmd = ReactiveCommand.CreateFromTask(ApplyPortAsync, notBusy);
        ApplyEndpointCmd = ReactiveCommand.CreateFromTask(ApplyEndpointAsync, notBusy);
        OpenLogCmd = ReactiveCommand.Create(() =>
        {
            if (File.Exists(WarpProxyManager.LogPath))
            {
                ProcUtils.ProcessStart(WarpProxyManager.LogPath);
            }
        });

        Refresh();

        // The proxy is usually started at app start, after this page was built; follow its real state.
        WarpProxyManager.Instance.StateChanged += () => RxSchedulers.MainThreadScheduler.Schedule(RefreshState);

        this.WhenAnyValue(x => x.Enabled).Skip(1).SubscribeAsync(async v =>
        {
            if (_loading)
            {
                return;
            }
            _config.WarpItem.Enabled = v;
            await ConfigHandler.SaveConfig(_config);
            if (v && IsRegistered && !WarpProxyManager.Instance.IsRunning)
            {
                await StartAsync();
            }
            else if (!v && WarpProxyManager.Instance.IsRunning)
            {
                await StopAsync();
            }
        });

        this.WhenAnyValue(x => x.Noise).Skip(1).SubscribeAsync(async v =>
        {
            if (_loading)
            {
                return;
            }
            _config.WarpItem.Noise = v;
            await ConfigHandler.SaveConfig(_config);
            await RunBusy(RestartIfRunning);
        });
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            var item = _config.WarpItem;
            IsRegistered = WarpManager.IsRegistered(item);
            Enabled = item.Enabled;
            Noise = item.Noise;
            PortText = item.Port.ToString();
            EndpointText = item.Endpoint;
            DeviceText = IsRegistered ? string.Format(AppText.WarpDevice, item.AddressV4) : AppText.WarpNotRegistered;
            RefreshState();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        IsRunning = WarpProxyManager.Instance.IsRunning;
        StateText = IsRunning ? AppText.WarpRunning : AppText.WarpStopped;
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

    private Task RegisterAsync() => RunBusy(async () =>
    {
        ProgressText = AppText.WarpRegistering;
        var (ok, message) = await Task.Run(() => WarpManager.RegisterAsync(_config));
        ProgressText = message;
        Refresh();
        if (ok && (_config.WarpItem.Enabled || WarpProxyManager.Instance.IsRunning))
        {
            await StartCore();
        }
    });

    private Task StartAsync() => RunBusy(StartCore);

    private async Task StartCore()
    {
        var (_, message) = await WarpProxyManager.Instance.StartAsync();
        ProgressText = message;
        Refresh();
        // +warp rules point at the proxy only while it runs.
        AppEvents.CoreReloadRequested.Publish();
    }

    private Task StopAsync() => RunBusy(async () =>
    {
        await WarpProxyManager.Instance.StopAsync();
        ProgressText = string.Empty;
        RefreshState();
        AppEvents.CoreReloadRequested.Publish();
    });

    private Task TestAsync() => RunBusy(async () =>
    {
        if (!WarpProxyManager.Instance.IsRunning)
        {
            var (ok, msg) = await WarpProxyManager.Instance.StartAsync();
            RefreshState();
            if (!ok)
            {
                ProgressText = msg;
                return;
            }
            AppEvents.CoreReloadRequested.Publish();
        }
        ProgressText = AppText.WarpTesting;
        var r = await WarpProxyManager.Instance.TestAsync();
        ProgressText = !r.Ok
            ? string.Format(AppText.WarpTestFailed, r.Error)
            : r.WarpOn
                ? string.Format(AppText.WarpTestOk, r.Colo, r.Location, r.Ms)
                : AppText.WarpTestNotWarp;
    });

    private Task ApplyPortAsync() => RunBusy(async () =>
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1024 or > 65535)
        {
            ProgressText = "1024–65535";
            return;
        }
        if (port == _config.WarpItem.Port)
        {
            return;
        }
        _config.WarpItem.Port = port;
        await ConfigHandler.SaveConfig(_config);
        await RestartIfRunning();
        Refresh();
    });

    private Task ApplyEndpointAsync() => RunBusy(async () =>
    {
        var endpoint = EndpointText.Trim();
        if (!WarpManager.IsValidEndpoint(endpoint))
        {
            ProgressText = AppText.WarpEndpointInvalid;
            return;
        }
        if (endpoint == _config.WarpItem.Endpoint)
        {
            return;
        }
        _config.WarpItem.Endpoint = endpoint;
        await ConfigHandler.SaveConfig(_config);
        await RestartIfRunning();
        Refresh();
    });

    private async Task RestartIfRunning()
    {
        if (!WarpProxyManager.Instance.IsRunning)
        {
            return;
        }
        var (_, msg) = await WarpProxyManager.Instance.StartAsync();
        ProgressText = msg;
        RefreshState();
        // The port may have changed.
        AppEvents.CoreReloadRequested.Publish();
    }
}
