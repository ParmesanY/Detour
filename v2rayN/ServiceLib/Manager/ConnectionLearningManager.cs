namespace ServiceLib.Manager;

/// <summary>
/// Learning mode: watches the live connection list of the sing-box core (TUN mode, Clash API)
/// and collects the domains/IPs a process connects to, so a process rule can be handed to zapret,
/// which only understands domains and IPs.
/// </summary>
public class ConnectionLearningManager
{
    private static readonly Lazy<ConnectionLearningManager> _instance = new(() => new());
    public static ConnectionLearningManager Instance => _instance.Value;

    private const string _tag = "ConnectionLearning";
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);

    // Second-level labels under which registrations happen one level deeper (example.co.uk).
    private static readonly HashSet<string> _secondLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "com", "net", "org", "gov", "edu", "ac", "or", "ne", "go", "msk", "spb",
    };

    /// <summary>
    /// Polls connections for <paramref name="duration" /> and merges what the rule's process used
    /// into <see cref="ConnectionRule.LearnedDomains" /> / <see cref="ConnectionRule.LearnedIps" />.
    /// Returns how many new entries were added.
    /// </summary>
    public async Task<(int Domains, int Ips)> LearnAsync(ConnectionRule rule, TimeSpan duration,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        if (ConnectionRuleParser.GetTargetKind(rule.Target) != EConnectionTarget.Process)
        {
            return (0, 0);
        }

        var domains = new HashSet<string>(rule.LearnedDomains ?? [], StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<string>(rule.LearnedIps ?? [], StringComparer.OrdinalIgnoreCase);
        var (startDomains, startIps) = (domains.Count, ips.Count);

        var until = DateTime.Now + duration;
        while (DateTime.Now < until && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connections = await ClashApiManager.Instance.GetConnections();
                var (d, i) = Collect(connections?.connections, rule.Target);
                var before = domains.Count + ips.Count;
                domains.UnionWith(d);
                ips.UnionWith(i);
                if (domains.Count + ips.Count != before && progress != null)
                {
                    await progress($"{rule.Target}: {domains.Count} domains, {ips.Count} IPs");
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        rule.LearnedDomains = [.. domains.Order(StringComparer.OrdinalIgnoreCase)];
        rule.LearnedIps = [.. ips.Order(StringComparer.Ordinal)];
        return (domains.Count - startDomains, ips.Count - startIps);
    }

    /// <summary>Domains (registrable part) and domain-less public IPs of connections made by the process.</summary>
    public static (HashSet<string> Domains, HashSet<string> Ips) Collect(IEnumerable<ConnectionItem>? connections, string processTarget)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in connections ?? [])
        {
            var meta = c.metadata;
            if (meta is null || !IsSameProcess(meta, processTarget))
            {
                continue;
            }

            var host = meta.host.IsNotEmpty() ? meta.host : meta.sniffHost;
            if (host.IsNotEmpty() && !IPAddress.TryParse(host, out _))
            {
                var domain = ToRegistrableDomain(host!);
                if (domain != null)
                {
                    domains.Add(domain);
                }
                continue;
            }

            var ipText = meta.destinationIP.IsNotEmpty() ? meta.destinationIP : host;
            if (IPAddress.TryParse(ipText, out var ip) && IsPublic(ip))
            {
                ips.Add(ip.ToString());
            }
        }
        return (domains, ips);
    }

    /// <summary>Process rule target may be a name ("Discord.exe") or a full path.</summary>
    public static bool IsSameProcess(MetadataItem meta, string processTarget)
    {
        var target = processTarget.Trim().Trim('"');
        var isPath = target.Contains('\\') || target.Contains('/');
        if (isPath)
        {
            return meta.processPath.IsNotEmpty() && string.Equals(
                Path.GetFullPath(meta.processPath!), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }

        var name = meta.process.IsNotEmpty() ? meta.process : Path.GetFileName(meta.processPath ?? "");
        return string.Equals(name, target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "cdn.discordapp.com" → "discordapp.com", "foo.bbc.co.uk" → "bbc.co.uk". A simple heuristic,
    /// good enough because zapret hostlists match subdomains anyway.
    /// </summary>
    public static string? ToRegistrableDomain(string host)
    {
        var labels = host.Trim().TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2 || labels.Any(l => l.Length > 63))
        {
            return null;
        }
        var take = labels.Length >= 3 && labels[^1].Length == 2 && _secondLevelSuffixes.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
        {
            return false;
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }
        var b = ip.GetAddressBytes();
        return !(b[0] == 10
                 || b[0] == 127
                 || b[0] == 0
                 || b[0] >= 224
                 || b[0] == 169 && b[1] == 254
                 || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                 || b[0] == 192 && b[1] == 168
                 || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                 || b[0] == 198 && b[1] is 18 or 19); // sing-box fake-ip / TUN ranges
    }
}
