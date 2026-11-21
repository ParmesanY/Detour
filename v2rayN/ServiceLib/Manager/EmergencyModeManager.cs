namespace ServiceLib.Manager;

/// <summary>
/// Emergency mode: when every VPN server is blocked, what normally goes through the VPN goes through Tor
/// (TorManager) instead; zapret and direct rules keep working as they are. Slow (1–5 Mbit/s), but enough to
/// pay for a new server, refresh a subscription and stay in messengers. On the way in torrents are blocked and
/// Windows updates held back (they would eat the little bandwidth there is). While it's on, the selected
/// server is re-checked every 5 minutes; when it works again the user is offered the way back.
/// Never turned on silently: failover only offers it.
/// </summary>
public sealed class EmergencyModeManager
{
    private static readonly Lazy<EmergencyModeManager> _instance = new(() => new());
    public static EmergencyModeManager Instance => _instance.Value;
    private const string _tag = "Emergency";

    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(5);

    private CancellationTokenSource? _watch;

    /// <summary>On, off, connecting, the server came back, an offer (any thread).</summary>
    public event Action? StateChanged;

    public bool IsActive => AppManager.Instance.Config.EmergencyItem?.Active == true;
    public bool IsConnecting { get; private set; }

    /// <summary>Failover ran out of servers: the emergency mode is worth offering.</summary>
    public bool Offered { get; private set; }

    /// <summary>The selected server answered again while in the emergency mode.</summary>
    public bool ServerBack { get; private set; }

    /// <summary>Windows Update hosts: held back while on Tor.</summary>
    public static readonly IReadOnlyList<string> UpdateDomains =
    [
        "domain:windowsupdate.com", "domain:update.microsoft.com", "domain:delivery.mp.microsoft.com",
        "domain:download.windowsupdate.com", "domain:dl.delivery.mp.microsoft.com",
    ];

    /// <summary>
    /// The routing rules of the emergency mode, in front of the user's: Tor's own processes direct, BitTorrent and
    /// Windows Update blocked.
    /// </summary>
    public static List<RulesItem> Rules() =>
    [
        TorManager.DirectRule(),
        new RulesItem
        {
            Id = Utils.GetGuid(false),
            Remarks = ConnectionRuleCompiler.RemarksPrefix + "emergency: torrents",
            OutboundTag = Global.BlockTag,
            Protocol = ["bittorrent"],
            Enabled = true,
            RuleType = ERuleType.Routing,
        },
        new RulesItem
        {
            Id = Utils.GetGuid(false),
            Remarks = ConnectionRuleCompiler.RemarksPrefix + "emergency: Windows Update",
            OutboundTag = Global.BlockTag,
            Domain = [.. UpdateDomains],
            Enabled = true,
            RuleType = ERuleType.Routing,
        },
    ];

    /// <summary>Failover has nothing left to try: offer the emergency mode (once, until dismissed or used).</summary>
    public void Offer()
    {
        var config = AppManager.Instance.Config;
        if (IsActive || Offered || config.EmergencyItem?.Offer == false || !TorManager.IsInstalled)
        {
            return;
        }
        Offered = true;
        Logging.SaveLog($"{_tag}: every server failed, offering the emergency mode");
        StateChanged?.Invoke();
    }

    public void DismissOffer()
    {
        Offered = false;
        StateChanged?.Invoke();
    }

    /// <summary>Starts Tor and, once it's connected, routes the VPN's traffic through it.</summary>
    public async Task<(bool Success, string Message)> ActivateAsync(IProgress<int>? progress = null)
    {
        if (IsConnecting)
        {
            return (false, AppText.TorConnecting);
        }
        IsConnecting = true;
        Offered = false;
        StateChanged?.Invoke();
        try
        {
            var (ok, message) = await TorManager.Instance.StartAsync(progress);
            if (!ok)
            {
                return (false, message);
            }
            var config = AppManager.Instance.Config;
            config.EmergencyItem.Active = true;
            await ConfigHandler.SaveConfig(config);
            Logging.SaveLog($"{_tag}: on (Tor, {TorManager.Instance.BridgeKind} bridges)");
            AppEvents.CoreReloadRequested.Publish();
            StartWatch();
            return (true, message);
        }
        finally
        {
            IsConnecting = false;
            StateChanged?.Invoke();
        }
    }

    public async Task DeactivateAsync()
    {
        _watch?.Cancel();
        _watch = null;
        ServerBack = false;
        var config = AppManager.Instance.Config;
        config.EmergencyItem.Active = false;
        await ConfigHandler.SaveConfig(config);
        Logging.SaveLog($"{_tag}: off");
        AppEvents.CoreReloadRequested.Publish();
        await TorManager.Instance.StopAsync();
        StateChanged?.Invoke();
    }

    /// <summary>App start: the mode was on when the app closed, so the servers are probably still blocked.</summary>
    public async Task ResumeAsync()
    {
        if (!IsActive)
        {
            return;
        }
        var (ok, message) = await ActivateAsync();
        if (!ok)
        {
            Logging.SaveLog($"{_tag}: couldn't resume: {message}");
            NoticeManager.Instance.Enqueue(message);
        }
    }

    private void StartWatch()
    {
        _watch?.Cancel();
        var cts = new CancellationTokenSource();
        _watch = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(RecheckEvery, cts.Token);
                    if (ServerBack || !IsActive)
                    {
                        continue;
                    }
                    if (await VpnStrategyManager.CheckCurrentServerAsync(AppManager.Instance.Config, cts.Token))
                    {
                        ServerBack = true;
                        Logging.SaveLog($"{_tag}: the selected server works again");
                        NoticeManager.Instance.Enqueue(AppText.EmergencyServerBack);
                        StateChanged?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        });
    }
}
