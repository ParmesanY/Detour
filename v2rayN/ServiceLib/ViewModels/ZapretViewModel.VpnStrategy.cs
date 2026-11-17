namespace ServiceLib.ViewModels;

/// <summary>zapret page: fragmentation strategies for the VPN connection and the server diagnosis.</summary>
public partial class ZapretViewModel
{
    /// <summary>"" = off, otherwise a <see cref="VpnStrategy.Name"/>; the value shows the description.</summary>
    public List<KeyValuePair<string, string>> VpnStrategies { get; } =
    [
        new("", AppText.VpnStrategyOff),
        .. VpnStrategyManager.Strategies.Select(s => new KeyValuePair<string, string>(s.Name, $"{s.Name} — {s.Description}")),
    ];

    [Reactive] public partial string VpnStrategy { get; set; } = string.Empty;
    [Reactive] public partial bool IsDiagnosing { get; set; }
    [Reactive] public partial string VpnDiagStatus { get; set; } = string.Empty;
    [Reactive] public partial string VpnDiagText { get; set; } = string.Empty;
    [Reactive] public partial string VpnDiagDetails { get; set; } = string.Empty;

    public ReactiveCommand<RxVoid, RxVoid> PickVpnStrategyCmd { get; private set; } = null!;

    private bool _settingStrategy;

    private void InitVpnStrategy()
    {
        // Fragmentation turned on by hand in the core settings (without a named strategy) shows as off here.
        VpnStrategy = VpnStrategyManager.Find(_config.ZapretItem.VpnStrategy)?.Name ?? string.Empty;
        this.WhenAnyValue(x => x.VpnStrategy).Skip(1).SubscribeAsync(async name =>
        {
            if (_settingStrategy || (name ?? "") == _config.ZapretItem.VpnStrategy)
            {
                return;
            }
            await ApplyVpnStrategyAsync(VpnStrategyManager.Find(name));
        });
        PickVpnStrategyCmd = ReactiveCommand.CreateFromTask(PickVpnStrategyAsync, this.WhenAnyValue(x => x.IsDiagnosing).Select(d => !d));
    }

    private async Task ApplyVpnStrategyAsync(VpnStrategy? strategy)
    {
        var wasWinws = VpnStrategyManager.Find(_config.ZapretItem.VpnStrategy) is { UsesWinws: true };
        VpnStrategyManager.Apply(_config, strategy);
        if (strategy is { UsesTransport: true })
        {
            await VpnStrategyManager.ApplyToServerAsync(_config, strategy);
        }
        await ConfigHandler.SaveConfig(_config);
        AppEvents.CoreReloadRequested.Publish();
        // winws strategies live in zapret's arguments: restart it with (or without) the VPN section.
        if ((wasWinws || strategy is { UsesWinws: true }) && ZapretManager.Instance.IsRunning)
        {
            await ZapretManager.Instance.StartAsync();
        }
    }

    private async Task PickVpnStrategyAsync()
    {
        IsDiagnosing = true;
        VpnDiagText = string.Empty;
        VpnDiagDetails = string.Empty;
        try
        {
            var progress = new Progress<string>(s => VpnDiagStatus = s);
            var report = await Task.Run(() => VpnStrategyManager.DiagnoseAsync(_config, progress));
            VpnDiagText = report.Text;
            VpnDiagDetails = string.Join(Environment.NewLine, report.Results.Select(r => string.Format(AppText.VpnStrategyResultLine,
                r.Strategy?.Name ?? AppText.VpnStrategyOff,
                r.Works ? string.Format(AppText.VpnStrategyOk, r.DelayMs) : r.Handshake ? AppText.VpnStrategyFreeze : AppText.VpnStrategyFail)));
            if (report.Best != null)
            {
                _settingStrategy = true;
                VpnStrategy = report.Best.Name;
                _settingStrategy = false;
                await ApplyVpnStrategyAsync(report.Best);
            }
        }
        finally
        {
            VpnDiagStatus = string.Empty;
            IsDiagnosing = false;
        }
    }
}
