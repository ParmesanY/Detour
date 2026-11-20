using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ServiceLib.Manager;

/// <summary>A hosting network on which Russian ISPs freeze TCP after a few packets (whole AS, any port).</summary>
/// <param name="Patterns">Words of the AS name (case-insensitive), e.g. "OVH", "HETZNER".</param>
public sealed record FrozenNetwork(string Name, List<string>? Patterns = null, List<int>? Asns = null);

/// <summary>Where the server sits and what the ISP does to it, for the server diagnosis.</summary>
public sealed record ServerNetwork(int Asn, string AsName, FrozenNetwork? Frozen);

/// <summary>
/// Network-level checks for the server diagnosis, over the physical network card (past any TUN):
/// ICMP to tell "the server's IP is blocked" (ping answers, TCP doesn't — net4people/Habr 1105268, Nov 2026)
/// from "the server is down", and the hosting network (AS) of the server, matched against networks Russian
/// ISPs freeze as a whole (Habr 1104904, 5–6 Nov 2026: ~25 packets on home lines, ~8 on mobile, then TCP
/// stops both ways; UDP — WireGuard/AmneziaWG — not touched yet).
/// </summary>
public static class ServerNetworkCheck
{
    private const string _tag = "ServerNetwork";

    private static readonly IReadOnlyList<FrozenNetwork> _builtin =
    [
        new("OVH", ["OVH"], [16276]),
        new("Hetzner", ["HETZNER"], [24940, 213230, 212317]),
        new("DigitalOcean", ["DIGITALOCEAN"], [14061]),
        new("Vultr", ["VULTR", "CHOOPA"], [20473]),
        new("M247", ["M247"], [9009]),
        new("Scaleway", ["SCALEWAY", "ONLINE S.A.S"], [12876]),
        new("GTHost", ["GTHOST"], [63023]),
        new("HostPapa", ["HOSTPAPA"], []),
        new("BuyVM", ["FRANTECH", "BUYVM"], [53667]),
        new("Clouvider", ["CLOUVIDER"], [62240]),
        new("Glesys", ["GLESYS"], [42708]),
        new("iomart", ["IOMART"], [20860]),
        new("TransitX", ["TRANSITX"], []),
    ];

    /// <summary>Published list, so it can follow the ISPs without a release (the measured set changes).</summary>
    public const string ListPath = "strategies/frozen-networks.json";
    private static string CachePath => Utils.GetConfigPath("frozenNetworks.json");
    private static volatile IReadOnlyList<FrozenNetwork> _downloaded = [];

    public static IReadOnlyList<FrozenNetwork> Networks => _downloaded.Count > 0 ? _downloaded : _builtin;

    public static string BuiltinJson() => JsonUtils.Serialize(_builtin.ToList());

    /// <summary>Validated entries from the list JSON; anything odd is dropped.</summary>
    public static List<FrozenNetwork> ParseList(string? json)
        => (JsonUtils.Deserialize<List<FrozenNetwork>>(json) ?? [])
            .Where(n => n != null && n.Name.IsNotEmpty() && n.Name.Length <= 40
                        && (n.Patterns?.Count > 0 || n.Asns?.Count > 0))
            .Select(n => n with
            {
                Patterns = (n.Patterns ?? []).Where(p => p.IsNotEmpty() && p.Length <= 40).ToList(),
                Asns = (n.Asns ?? []).Where(a => a > 0).ToList(),
            })
            .ToList();

    public static void LoadCachedList()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                _downloaded = ParseList(File.ReadAllText(CachePath));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static async Task UpdateListAsync(HttpClient client, CancellationToken ct)
    {
        if (AppUpdateManager.Repo is not { Length: > 0 } repo)
        {
            return;
        }
        try
        {
            var json = await client.GetStringAsync($"https://raw.githubusercontent.com/{repo}/main/{ListPath}", ct);
            var list = ParseList(json);
            if (list.Count > 0)
            {
                _downloaded = list;
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Logging.SaveLog($"{_tag}: list: {ex.Message}");
        }
    }

    /// <summary>The frozen network the AS belongs to, by number or by a word of its name; null if none.</summary>
    public static FrozenNetwork? FindFrozen(int asn, string? asName, IReadOnlyList<FrozenNetwork>? list = null)
    {
        list ??= Networks;
        var name = (asName ?? "").ToUpperInvariant();
        return list.FirstOrDefault(n => (asn > 0 && (n.Asns ?? []).Contains(asn))
                                        || (name.Length > 0 && (n.Patterns ?? []).Any(p => name.Contains(p.ToUpperInvariant(), StringComparison.Ordinal))));
    }

    private static readonly ConcurrentDictionary<string, (bool Frozen, int Asn, DateTime At)> _hostCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The AS of a server host from the last lookup (FrozenServersAsync), or 0 when unknown.</summary>
    public static int CachedAsn(string? host)
        => host != null && _hostCache.TryGetValue(host, out var v) ? v.Asn : 0;

    /// <summary>UDP protocols: the packet-count freeze is TCP only (so far).</summary>
    public static bool UsesUdp(ProfileItem p)
        => p.ConfigType is EConfigType.Hysteria2 or EConfigType.TUIC or EConfigType.WireGuard;

    /// <summary>
    /// Servers (by IndexId) whose TCP goes to a frozen hosting network, for the failover order. Looks up at most
    /// <paramref name="max" /> hosts, cached for a day; UDP servers never count as frozen.
    /// </summary>
    public static async Task<HashSet<string>> FrozenServersAsync(IEnumerable<ProfileItem> servers, CancellationToken ct, int max = 12)
    {
        var tcp = servers.Where(s => !UsesUdp(s) && s.Address.IsNotEmpty()).ToList();
        var hosts = tcp.Select(s => s.Address).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        await Task.WhenAll(hosts.Select(async host =>
        {
            if (_hostCache.TryGetValue(host, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromDays(1))
            {
                return;
            }
            try
            {
                var ips = await ZapretWatchdog.ResolveDirectAsync(host.Trim('[', ']'), ZapretWatchdog.PhysicalIPv4Address(), ct);
                var ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                var network = ip != null ? await LookupAsync(ip, ct) : null;
                if (network != null)
                {
                    _hostCache[host] = (network.Frozen != null, network.Asn, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // unknown: not frozen
            }
        }));
        return [.. tcp.Where(s => _hostCache.TryGetValue(s.Address, out var v) && v.Frozen).Select(s => s.IndexId)];
    }

    // ---- AS lookup ----

    /// <summary>
    /// The server's AS via Team Cymru's DNS (origin.asn.cymru.com, then the AS name), asked over DoH by IP
    /// (Cloudflare) on the physical card: the ISP's DNS can't answer TXT honestly and the TUN must be skipped.
    /// Sends the server's IP (not the user's) to Cloudflare/Cymru. Null when unknown.
    /// </summary>
    public static async Task<ServerNetwork?> LookupAsync(IPAddress ip, CancellationToken ct)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var reversed = string.Join('.', ip.GetAddressBytes().Reverse());
            var origin = await TxtAsync($"{reversed}.origin.asn.cymru.com", cts.Token);
            var asn = ParseOriginAsn(origin);
            if (asn <= 0)
            {
                return null;
            }
            var asName = ParseAsName(await TxtAsync($"AS{asn}.asn.cymru.com", cts.Token));
            return new(asn, asName, FindFrozen(asn, asName));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: AS lookup: {ex.Message}");
            return null;
        }
    }

    /// <summary>"16276 | 51.68.0.0/16 | FR | ripencc | 2014-09-05" → 16276 (first AS of a multi-origin answer).</summary>
    public static int ParseOriginAsn(string? txt)
    {
        var first = (txt ?? "").Trim('"', ' ').Split('|')[0].Trim().Split(' ')[0];
        return int.TryParse(first, out var asn) ? asn : 0;
    }

    /// <summary>"16276 | FR | ripencc | 2001-06-14 | OVH, FR" → "OVH, FR".</summary>
    public static string ParseAsName(string? txt)
    {
        var parts = (txt ?? "").Trim('"', ' ').Split('|');
        return parts.Length >= 5 ? parts[4].Trim() : string.Empty;
    }

    private static async Task<string?> TxtAsync(string name, CancellationToken ct)
    {
        var local = ZapretWatchdog.PhysicalIPv4Address();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (ctx, c) => new ValueTask<Stream>(ZapretWatchdog.ConnectBoundAsync(
                [IPAddress.Parse(ctx.DnsEndPoint.Host)], ctx.DnsEndPoint.Port, local, c)),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://1.1.1.1/dns-query?type=TXT&name={Uri.EscapeDataString(name)}");
        req.Headers.Accept.ParseAdd("application/dns-json");
        using var resp = await client.SendAsync(req, ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (json?["Answer"] as JsonArray)?.Select(a => a?["data"]?.GetValue<string>()).FirstOrDefault(d => d.IsNotEmpty());
    }

    // ---- ICMP ----

    /// <summary>Whether the address answers ping from the physical card's address (past the TUN).</summary>
    public static async Task<bool> PingAsync(IPAddress ip, CancellationToken ct)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var local = ZapretWatchdog.PhysicalIPv4Address();
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            var ok = Utils.IsWindows() && local != null
                ? await Task.Run(() => PingFrom(local, ip, 2000), ct)
                : await PingPlainAsync(ip);
            if (ok)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> PingPlainAsync(IPAddress ip)
    {
        try
        {
            using var ping = new Ping();
            return (await ping.SendPingAsync(ip, 2000)).Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr icmpHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress, byte[] requestData, ushort requestSize, IntPtr requestOptions,
        byte[] replyBuffer, uint replySize, uint timeout);

    /// <summary>ICMP echo with an explicit source address, so Windows sends it through that card, not the TUN.</summary>
    private static bool PingFrom(IPAddress source, IPAddress dest, uint timeoutMs)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }
        try
        {
            var data = new byte[32];
            var reply = new byte[64 + data.Length + 8];
            var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(source.GetAddressBytes(), 0), BitConverter.ToUInt32(dest.GetAddressBytes(), 0),
                data, (ushort)data.Length, IntPtr.Zero, reply, (uint)reply.Length, timeoutMs);
            // ICMP_ECHO_REPLY: Address (4 bytes), then Status (0 = IP_SUCCESS).
            return count > 0 && BitConverter.ToUInt32(reply, 4) == 0;
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }
}
