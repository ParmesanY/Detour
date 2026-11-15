using System.Net.NetworkInformation;

namespace ServiceLib.Common;

/// <summary>
/// Finds other VPN / proxy / DPI-bypass apps that fight with this client over routes, TUN and
/// WinDivert. Two TUN clients at once usually means no site loads at all.
/// </summary>
public static class VpnConflictDetector
{
    // Process name (without .exe) → name to show.
    private static readonly Dictionary<string, string> _knownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Throne"] = "Throne",
        ["ThroneCore"] = "Throne",
        ["nekoray"] = "NekoRay",
        ["nekobox"] = "NekoBox",
        ["Happ"] = "Happ",
        ["incy"] = "INCY",
        ["Hiddify"] = "Hiddify",
        ["HiddifyCli"] = "Hiddify",
        ["Koala Clash"] = "Koala Clash",
        ["clash-verge"] = "Clash Verge",
        ["verge-mihomo"] = "Clash Verge",
        ["FlClash"] = "FlClash",
        ["Prizrak-Box"] = "Prizrak-Box",
        ["ProtonVPN"] = "Proton VPN",
        ["ProtonVPNService"] = "Proton VPN",
        ["AmneziaVPN"] = "AmneziaVPN",
        ["AmneziaWG"] = "AmneziaWG",
        ["warp-svc"] = "Cloudflare WARP",
        ["goodbyedpi"] = "GoodbyeDPI",
        ["v2rayN"] = "v2rayN",
        ["xray"] = "Xray",
        ["sing-box"] = "sing-box",
    };

    // Our own TUN adapters (sing-box / xray as started by this client).
    private static readonly string[] _ownTunNames = ["singbox_tun", "xray_tun"];

    /// <summary>Names of conflicting apps / adapters currently active; empty if none.</summary>
    public static List<string> Detect()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownDir = Path.GetFullPath(Utils.StartupPath());
        var ownPid = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == ownPid || !_knownApps.TryGetValue(p.ProcessName, out var name))
                {
                    continue;
                }
                // Cores and a v2rayN started from this client's own folder are ours.
                var path = SafePath(p);
                if (path != null && Path.GetFullPath(path).StartsWith(ownDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                found.Add(name);
            }
            catch
            {
                // Access denied for some system processes: skip.
            }
            finally
            {
                p.Dispose();
            }
        }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || _ownTunNames.Any(t => ni.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var desc = ni.Description;
                if (desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("sing-tun", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{AppText.ConflictTunAdapter} «{ni.Name}»");
                }
            }
        }
        catch
        {
        }

        return [.. found];
    }

    private static string? SafePath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
