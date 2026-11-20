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
    /// <param name="allowCustom">Custom (full Xray JSON) profiles can be picked too: the main server is one
    /// of them, so the rule server is added into its config (<see cref="CustomConfigRuleServers"/>).</param>
    public static async Task<ProfileItem?> ResolveAsync(string outboundTag, ProfileItem main, bool allowCustom = false)
    {
        var name = outboundTag[TagPrefix.Length..];
        var (items, candidates) = await LoadCandidatesAsync(allowCustom);
        var names = SplitList(name);
        var id = names.Count > 1
            ? PickFirstAlive(names, main.IndexId, main.Subid, candidates)
            : Pick(name, main.IndexId, main.Subid, candidates);
        var picked = id == null ? null : items.First(p => p.IndexId == id);
        if (picked != null)
        {
            Logging.SaveLog($"ServiceServer: \"{name}\" -> {picked.Remarks}");
        }
        return picked;
    }

    /// <summary>"A; B; C": the server names of a list, in order (a single name gives one item).</summary>
    public static List<string> SplitList(string? name)
        => (name ?? "").Split(';').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();

    public static bool IsList(string outboundTag)
        => outboundTag.StartsWith(TagPrefix, StringComparison.Ordinal) && SplitList(outboundTag[TagPrefix.Length..]).Count > 1;

    /// <summary>
    /// A server list without live switching (the Xray core): the first found server that didn't fail its
    /// last ping. Null for the main server or when none is left.
    /// </summary>
    public static string? PickFirstAlive(IReadOnlyList<string> names, string mainIndexId, string? mainSubid, IReadOnlyList<Candidate> all)
    {
        foreach (var c in FindAll(names, mainSubid, all))
        {
            if (c.Delay >= 0)
            {
                return c.IndexId == mainIndexId ? null : c.IndexId;
            }
        }
        return null;
    }

    /// <summary>The servers of a list that exist, in the list's order, without repeats.</summary>
    public static List<Candidate> FindAll(IReadOnlyList<string> names, string? mainSubid, IReadOnlyList<Candidate> all)
    {
        var pool = all.Where(c => mainSubid.IsNullOrEmpty() || c.Subid == mainSubid).ToList();
        var result = new List<Candidate>();
        foreach (var name in names)
        {
            var c = Find(pool, name) ?? Find(all, name);
            if (c != null && result.All(r => r.IndexId != c.IndexId))
            {
                result.Add(c);
            }
            else if (c == null)
            {
                Logging.SaveLog($"ServiceServer: \"{name}\" not found");
            }
        }
        return result;
    }

    /// <summary>
    /// The profiles of a server list for live switching, in order; null stands for the main server.
    /// </summary>
    public static async Task<List<ProfileItem?>> ResolveListAsync(string outboundTag, ProfileItem main, bool allowCustom)
    {
        var (items, candidates) = await LoadCandidatesAsync(allowCustom);
        var found = FindAll(SplitList(outboundTag[TagPrefix.Length..]), main.Subid, candidates);
        Logging.SaveLog($"ServiceServer: \"{outboundTag[TagPrefix.Length..]}\" -> {string.Join(", ", found.Select(c => c.Remarks))}");
        return found.Select(c => c.IndexId == main.IndexId ? null : items.First(p => p.IndexId == c.IndexId)).ToList();
    }

    private static async Task<(List<ProfileItem> Items, List<Candidate> Candidates)> LoadCandidatesAsync(bool allowCustom)
    {
        var items = await AppManager.Instance.ProfileItems("") ?? [];
        var exs = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(e => e.IndexId, e => e.Delay);
        var candidates = items
            .Where(p => !p.ConfigType.IsComplexType()
                        || (allowCustom && p.ConfigType == EConfigType.Custom
                            && AppManager.Instance.GetCoreType(p, p.ConfigType) == ECoreType.Xray))
            .Select(p => new Candidate(p.IndexId, p.Remarks ?? "", p.Subid, exs.GetValueOrDefault(p.IndexId)))
            .ToList();
        return (items, candidates);
    }
}
