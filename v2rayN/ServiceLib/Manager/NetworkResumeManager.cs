using System.Net.NetworkInformation;

namespace ServiceLib.Manager;

/// <summary>
/// Reconnects after sleep and after a network change. Windows keeps the old core sockets and can drop
/// TUN routes on resume: the icon stays green while nothing loads (a user's log: 30+ minutes dead after
/// waking until a manual reconnect). Sleep is seen as a jump of the wall clock between ticks; a network
/// change as a new default physical card / gateway / address (our own TUN adapter doesn't count). After
/// the event settles and the plain internet answers, the core is reloaded once, and winws is restarted
/// when the physical card changed (it is bound to the card index, --wf-iface).
/// </summary>
public sealed class NetworkResumeManager
{
    private static readonly Lazy<NetworkResumeManager> _instance = new(() => new());
    public static NetworkResumeManager Instance => _instance.Value;
    private const string _tag = "NetworkResume";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WaitForInternet = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinBetweenReconnects = TimeSpan.FromSeconds(60);
    private const string InternetProbeUrl = "https://ya.ru/";

    private CancellationTokenSource? _cts;
    private string? _fingerprint;
    private DateTime _lastReconnect = DateTime.MinValue;
    private volatile bool _addressChanged;

    /// <summary>Raised (any thread) when the core should be reloaded; true = the physical card changed.</summary>
    public event Action<bool>? ReconnectRequested;

    public void Start()
    {
        if (_cts != null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _fingerprint = Fingerprint();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _cts?.Cancel();
        _cts = null;
    }

    private void OnAddressChanged(object? sender, EventArgs e) => _addressChanged = true;

    private async Task LoopAsync(CancellationToken token)
    {
        var last = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, token);
                var now = DateTime.UtcNow;
                var gap = now - last;
                var slept = IsSleepGap(Tick, gap);
                last = now;
                if (!slept && !_addressChanged)
                {
                    continue;
                }
                if (slept)
                {
                    Logging.SaveLog($"{_tag}: woke up, the PC was asleep for about {(int)gap.TotalMinutes} min");
                }
                await HandleAsync(slept, token);
                last = DateTime.UtcNow;
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

    private async Task HandleAsync(bool slept, CancellationToken token)
    {
        // Address changes come in bursts (DHCP, IPv6, adapters waking up): wait until they stop.
        do
        {
            _addressChanged = false;
            await Task.Delay(Settle, token);
        } while (_addressChanged);

        var oldFingerprint = _fingerprint;
        var newFingerprint = Fingerprint();
        if (!ShouldReconnect(slept, oldFingerprint, newFingerprint))
        {
            return;
        }
        if (AppManager.Instance.Config.ResumeReconnect == false || AppManager.Instance.Config.IndexId.IsNullOrEmpty())
        {
            _fingerprint = newFingerprint;
            return;
        }
        if (DateTime.Now - _lastReconnect < MinBetweenReconnects)
        {
            _fingerprint = newFingerprint;
            return;
        }

        // Reloading before the network is up just fails again: wait for the plain internet.
        var deadline = DateTime.UtcNow + WaitForInternet;
        while (!await ZapretWatchdog.ProbeAsync(InternetProbeUrl, token))
        {
            if (DateTime.UtcNow > deadline)
            {
                Logging.SaveLog($"{_tag}: no internet {WaitForInternet.TotalSeconds} s after the change, not reconnecting");
                return;
            }
            await Task.Delay(Tick, token);
        }

        newFingerprint = Fingerprint();
        _fingerprint = newFingerprint;
        _lastReconnect = DateTime.Now;
        var cardChanged = CardOf(oldFingerprint) != CardOf(newFingerprint);
        Logging.SaveLog($"{_tag}: {(slept ? "after sleep" : "network changed")}, reconnecting{(cardChanged ? " (new network card)" : "")}");
        ReconnectRequested?.Invoke(cardChanged);
    }

    /// <summary>A tick that took far longer than its delay: the PC was asleep or hibernated.</summary>
    public static bool IsSleepGap(TimeSpan expected, TimeSpan actual) => actual - expected > SleepGap;

    /// <summary>
    /// Reconnect after sleep, or when the physical network is a different one. A null fingerprint (no
    /// network yet / no network before) is not a change: the reconnect waits for the network to show up.
    /// </summary>
    public static bool ShouldReconnect(bool slept, string? oldFingerprint, string? newFingerprint)
        => slept || (newFingerprint != null && oldFingerprint != newFingerprint);

    private static string? CardOf(string? fingerprint) => fingerprint?.Split('|')[0];

    /// <summary>"card|gateway|address" of the default physical network card, or null when there is none.</summary>
    private static string? Fingerprint()
    {
        try
        {
            var name = NetworkUtils.GetDefaultPhysicalInterfaceName();
            if (name.IsNullOrEmpty())
            {
                return null;
            }
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            if (ni == null || ni.OperationalStatus != OperationalStatus.Up)
            {
                return null;
            }
            var ip = ni.GetIPProperties();
            var gateway = ip.GatewayAddresses.Select(g => g.Address.ToString()).OrderBy(s => s).FirstOrDefault();
            var address = ip.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).OrderBy(s => s).FirstOrDefault();
            return $"{name}|{gateway}|{address}";
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }
}
