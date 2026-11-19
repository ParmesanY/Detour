using System.Net.NetworkInformation;

namespace ServiceLib.Manager;

/// <summary>The zapret strategy that worked on one network.</summary>
public sealed class NetworkProfile
{
    public string Name { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty;
    public string? Desync { get; set; }
    public string? DesyncNote { get; set; }
    public string SiteRecords { get; set; } = string.Empty;
    public DateTime Updated { get; set; }
}

/// <summary>
/// Strategies per network: the one that works at home on one ISP fails on a phone hotspot (MegaFon, MTS) or at
/// another ISP, and people had to pick again after every switch. The network is told by its Wi-Fi name, or for a
/// cable by the router's MAC address; a pick (automatic or by hand) is remembered for it, and coming back to a
/// known network brings its strategy back. An unknown network keeps the current strategy and gets a hint.
/// Stored in guiConfigs/networkProfiles.json.
/// </summary>
public static class NetworkProfiles
{
    private const string _tag = "NetworkProfiles";
    private static readonly Lock _gate = new();
    private static readonly HashSet<string> _hinted = [];

    private static string StorePath => Utils.GetConfigPath("networkProfiles.json");

    public sealed record NetworkId(string Key, string Name);

    /// <summary>The key of a network: Wi-Fi by name, otherwise the router's MAC; null when neither is known.</summary>
    public static NetworkId? IdFor(string? ssid, string? gatewayMac, string? cardName)
    {
        if (ssid.IsNotEmpty())
        {
            return new($"wifi:{ssid}", ssid!);
        }
        if (gatewayMac.IsNotEmpty() && gatewayMac != "00-00-00-00-00-00")
        {
            return new($"gw:{gatewayMac}", cardName.IsNullOrEmpty() ? gatewayMac! : $"{cardName} ({gatewayMac})");
        }
        return null;
    }

    /// <summary>"SSID : name" from netsh output; the BSSID and profile lines don't count.</summary>
    public static string? ParseSsid(string? netshOutput)
    {
        foreach (var line in (netshOutput ?? "").Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*SSID\s*:\s*(.+?)\s*$");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>The network Detour's physical traffic goes through now; null without one (or off Windows).</summary>
    public static NetworkId? Current()
    {
        if (!Utils.IsWindows())
        {
            return null;
        }
        try
        {
            var name = NetworkUtils.GetDefaultPhysicalInterfaceName();
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            if (ni is null || ni.OperationalStatus != OperationalStatus.Up)
            {
                return null;
            }
            var ssid = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? CurrentSsid() : null;
            var gateway = ni.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return IdFor(ssid, gateway is null ? null : GatewayMac(gateway), ni.Name);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>
    /// Wi-Fi name through netsh. Windows 11 24H2 hides it from apps without location access; then the router's
    /// MAC is used instead.
    /// </summary>
    private static string? CurrentSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // netsh writes in the console's OEM code page (866 on Russian Windows): a Cyrillic Wi-Fi name in UTF-8 would be garbage.
                StandardOutputEncoding = OemEncoding(),
            };
            foreach (var a in new[] { "wlan", "show", "interfaces" })
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ParseSsid(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);

    private static string? GatewayMac(IPAddress gateway)
    {
        var mac = new byte[6];
        var len = mac.Length;
        var ip = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);
        return SendARP(ip, 0, mac, ref len) == 0 && len == 6 ? string.Join("-", mac.Select(b => b.ToString("X2"))) : null;
    }

    public static Dictionary<string, NetworkProfile> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonUtils.Deserialize<Dictionary<string, NetworkProfile>>(File.ReadAllText(StorePath)) ?? []
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void Save(Dictionary<string, NetworkProfile> all)
    {
        try
        {
            File.WriteAllText(StorePath, JsonUtils.Serialize(all));
        }
        catch (IOException ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static NetworkProfile From(ZapretItem item, string name) => new()
    {
        Name = name,
        Strategy = item.Strategy,
        Desync = item.Desync,
        DesyncNote = item.DesyncNote,
        SiteRecords = item.SiteRecords,
        Updated = DateTime.Now,
    };

    public static bool SameStrategy(NetworkProfile p, ZapretItem item) =>
        p.Strategy == item.Strategy && p.Desync == item.Desync && p.SiteRecords == item.SiteRecords;

    /// <summary>Remembers the current strategy for the current network (after a pick that worked, or a manual one).</summary>
    public static void RememberCurrent(ZapretItem item)
    {
        if (Current() is not { } net)
        {
            return;
        }
        lock (_gate)
        {
            var all = Load();
            all[net.Key] = From(item, net.Name);
            Save(all);
        }
        Logging.SaveLog($"{_tag}: \"{net.Name}\" -> {ZapretManager.DisplayStrategy(item)}");
    }

    /// <summary>
    /// After a network change: a known network gets its strategy back (winws restarted, lists synced, the cores
    /// reloaded when the record split changes); an unknown one keeps the current strategy and gets a hint once.
    /// </summary>
    public static async Task ApplyForCurrentAsync(Config config)
    {
        var item = config.ZapretItem;
        if (!item.Enabled || !ZapretManager.Instance.IsInstalled || ZapretManager.UsesTpws || ZapretManager.Instance.IsSelecting)
        {
            return;
        }
        if (Current() is not { } net)
        {
            return;
        }
        NetworkProfile? saved;
        lock (_gate)
        {
            saved = Load().GetValueOrDefault(net.Key);
        }
        if (saved is null)
        {
            if (_hinted.Add(net.Key))
            {
                NoticeManager.Instance.Enqueue(string.Format(AppText.NetworkProfileNew, net.Name));
            }
            return;
        }
        if (SameStrategy(saved, item) || !ZapretManager.Instance.GetStrategies().Contains(saved.Strategy))
        {
            return;
        }
        var recordsChanged = saved.SiteRecords != item.SiteRecords;
        (item.Strategy, item.Desync, item.DesyncNote, item.SiteRecords) = (saved.Strategy, saved.Desync, saved.DesyncNote, saved.SiteRecords);
        await ConfigHandler.SaveConfig(config);
        if (ZapretManager.Instance.IsRunning)
        {
            await ZapretManager.Instance.StartAsync();
        }
        await ConnectionRulesManager.Instance.SyncZapretAsync(config);
        if (recordsChanged)
        {
            AppEvents.CoreReloadRequested.Publish();
        }
        Logging.SaveLog($"{_tag}: \"{net.Name}\": back to {ZapretManager.DisplayStrategy(item)}");
        NoticeManager.Instance.Enqueue(string.Format(AppText.NetworkProfileApplied, net.Name, ZapretManager.DisplayStrategy(item)));
    }

    /// <summary>Forgets one network (UI) or all of them.</summary>
    public static void Forget(string? key = null)
    {
        lock (_gate)
        {
            var all = Load();
            if (key is null)
            {
                all.Clear();
            }
            else
            {
                all.Remove(key);
            }
            Save(all);
        }
    }
}
