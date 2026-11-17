namespace ServiceLib.Manager;

/// <summary>
/// "Servers per service": a connection rule can send its traffic through its own VPN server
/// ("@youtube +vpn = 🇩🇪 Germany 1", or "= auto"). The compiler writes the outbound as
/// "server:&lt;name&gt;"; here it becomes a real server: the named one, or the best by last ping when the
/// name is "auto" or the named server failed its last ping. When the named server answers again
/// (a later ping test), the next reload goes back to it.
/// </summary>
public static class ServiceServerResolver
{
    public const string TagPrefix = "server:";

    public record Candidate(string IndexId, string Remarks, string? Subid, int Delay);

    /// <summary>
    /// Picks the server for a rule. Returns null to use the main server (no match, nothing better, or
    /// the pick is the main server itself).
    /// </summary>
    public static string? Pick(string name, string mainIndexId, string? mainSubid, IReadOnlyList<Candidate> all)
    {
        // Servers of the main server's subscription first: another subscription may be expired.
        var pool = all.Where(c => mainSubid.IsNullOrEmpty() || c.Subid == mainSubid).ToList();
        if (pool.Count == 0)
        {
            pool = all.ToList();
        }

        Candidate? pick = null;
        if (!ConnectionRuleParser.IsAutoServer(name))
        {
            pick = Find(pool, name) ?? Find(all, name);
            if (pick is { Delay: < 0 })
            {
                pick = null; // failed its last ping: take the best one meanwhile
            }
            else if (pick != null)
            {
                return pick.IndexId == mainIndexId ? null : pick.IndexId;
            }
        }

        pick = pool.Where(c => c.Delay > 0).OrderBy(c => c.Delay).FirstOrDefault();
        return pick == null || pick.IndexId == mainIndexId ? null : pick.IndexId;
    }

    private static Candidate? Find(IEnumerable<Candidate> list, string name)
    {
        var items = list.ToList();
        return items.FirstOrDefault(c => c.Remarks.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? items.FirstOrDefault(c => c.Remarks.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The profile for a "server:…" outbound tag, or null for the main server.</summary>
    public static async Task<ProfileItem?> ResolveAsync(string outboundTag, ProfileItem main)
    {
        var name = outboundTag[TagPrefix.Length..];
        var items = await AppManager.Instance.ProfileItems("") ?? [];
        var exs = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(e => e.IndexId, e => e.Delay);
        var candidates = items
            .Where(p => !p.ConfigType.IsComplexType() && p.ConfigType != EConfigType.Custom)
            .Select(p => new Candidate(p.IndexId, p.Remarks ?? "", p.Subid, exs.GetValueOrDefault(p.IndexId)))
            .ToList();
        var id = Pick(name, main.IndexId, main.Subid, candidates);
        var picked = id == null ? null : items.First(p => p.IndexId == id);
        if (picked != null)
        {
            Logging.SaveLog($"ServiceServer: \"{name}\" -> {picked.Remarks}");
        }
        return picked;
    }
}
