using Detour.NetProbe;

namespace ServiceLib.Manager;

/// <summary>
/// The Detour Map strategy registry from Detour's side: hints for the strategy picker (what worked for other people on
/// this ISP) and, with the Detour Map opt-in on, the picker's results back. Every hint is still checked here like any
/// other strategy, so a bad entry only costs a few seconds. Requests go out over the physical network card: the
/// collector tells the ISP from the connection, and through a VPN it would see the VPN's network.
/// </summary>
public static class StrategyRegistry
{
    private const string _tag = "StrategyRegistry";
    private const int MaxHints = 5;
    /// <summary>The registry takes at most 10 attempts per strategy.</summary>
    private const int MaxShared = 10;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    public sealed record Candidate(string Name, string Desync, string Note);

    /// <summary>
    /// New strategies from community findings, tried by the picker after the registry hints and before the .bat files.
    /// With the Detour Map opt-in their results go to the registry like any other, so within days the map shows on which
    /// ISPs they work; the ones that do then come back to everyone as ordinary registry hints. Current batch (2026-11):
    /// fakes carrying a Russian name instead of google.com (Flowseal #17334 measured 230 KB/s against 48 B/s).
    /// </summary>
    public static IReadOnlyList<Candidate> Experimental =>
    [
        new($"{ZapretDesync.RegistryPrefix}exp-fake-vk",
            "--dpi-desync=fake,multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com",
            AppText.ZapretExperimentalNote),
        new($"{ZapretDesync.RegistryPrefix}exp-fake-max",
            "--dpi-desync=fake,multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls-mod=rnd,dupsid,sni=max.ru",
            AppText.ZapretExperimentalNote),
        new($"{ZapretDesync.RegistryPrefix}exp-hostfakesplit-vk",
            "--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com --dpi-desync-hostfakesplit-mod=host=vk.com,altorder=1 --dpi-desync-fooling=ts",
            AppText.ZapretExperimentalNote),
    ];

    /// <summary>Registry hints first, then the experiments not already among them (same args = same strategy).</summary>
    public static List<Candidate> WithExperiments(IReadOnlyList<Candidate> hints, bool enabled)
    {
        if (!enabled || ZapretManager.UsesTpws)
        {
            return [.. hints];
        }
        var known = hints.Select(h => h.Desync).ToHashSet(StringComparer.Ordinal);
        return [.. hints, .. Experimental.Where(e => ZapretDesync.Tokens(e.Desync) is not null && !known.Contains(e.Desync))];
    }

    /// <summary>Registry strategies for this ISP, ready for the picker; empty when off, unreachable or nothing fits.</summary>
    public static async Task<List<Candidate>> CandidatesAsync(Config config, IReadOnlyList<string> urls, CancellationToken ct)
    {
        if (!config.ZapretItem.RegistryHints || ZapretManager.UsesTpws)
        {
            return [];
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            var hints = await Registry.GetHintsAsync(new Uri(BlockMapManager.Endpoint), StrategyKey.Winws, TargetFor(urls), NicInfo.Detect(), cts.Token);
            if (hints is null)
            {
                return [];
            }
            var isp = hints.AsOrg.IsNullOrEmpty() ? $"AS{hints.Asn}" : hints.AsOrg!;
            var list = new List<Candidate>();
            foreach (var h in hints.Strategies)
            {
                var tokens = ZapretDesync.Tokens(h.Args);
                if (tokens is null || !FilesExist(tokens))
                {
                    continue;
                }
                list.Add(new($"{ZapretDesync.RegistryPrefix}{h.Id}", string.Join(' ', tokens), string.Format(AppText.ZapretRegistryNote, h.Works, h.Reporters, isp)));
                if (list.Count >= MaxHints)
                {
                    break;
                }
            }
            Logging.SaveLog($"{_tag}: {list.Count} hints for {isp}");
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: hints unavailable: {ex.Message}");
            return [];
        }
    }

    private static bool FilesExist(IEnumerable<string> tokens) =>
        tokens.SelectMany(t => Regex.Matches(t, @"%BIN%([\w.\-]+)").Select(m => m.Groups[1].Value))
            .All(f => File.Exists(Path.Combine(ZapretManager.BinDir, f)));

    /// <summary>A target ID when every checked URL is the same public target, otherwise null (any site).</summary>
    public static string? TargetFor(IEnumerable<string> urls)
    {
        var ids = urls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? Registry.TargetIdForHost(uri.Host) : null).Distinct().ToList();
        return ids is [{ } id] ? id : null;
    }

    // ---- sharing ----

    /// <summary>Results are shared only with the Detour Map opt-in (the same one as block measurements).</summary>
    public static bool Sharing(Config config) => config.BlockMapItem.Enabled && !ZapretManager.UsesTpws;

    /// <summary>
    /// URLs the general profile handles: the ones on the Google list have a profile of their own in Flowseal's
    /// strategies, so a registry strategy (general profile only) says nothing about them.
    /// </summary>
    public static List<int> GeneralIndexes(IReadOnlyList<string> urls, IReadOnlySet<string> googleHosts)
    {
        var list = new List<int>();
        for (var i = 0; i < urls.Count; i++)
        {
            if (Uri.TryCreate(urls[i], UriKind.Absolute, out var uri) && !OnList(uri.Host, googleHosts))
            {
                list.Add(i);
            }
        }
        return list;
    }

    private static bool OnList(string host, IReadOnlySet<string> hosts)
    {
        var parts = host.ToLowerInvariant().Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (hosts.Contains(string.Join('.', parts[i..])))
            {
                return true;
            }
        }
        return false;
    }

    public static HashSet<string> GoogleHosts()
    {
        try
        {
            var path = Path.Combine(ZapretManager.ListsDir, "list-google.txt");
            return File.Exists(path)
                ? [.. File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && !l.StartsWith('#'))]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The baseline: the checked URLs with no bypass at all. winws must be stopped by the caller. Null when another
    /// DPI bypass is running (its results would mean nothing to the registry).
    /// </summary>
    public static async Task<bool[]?> BaselineAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        if (Prober.DpiBypassRunning())
        {
            Logging.SaveLog($"{_tag}: another DPI bypass is running, results won't be shared");
            return null;
        }
        return await Task.WhenAll(urls.Select(u => ZapretWatchdog.ProbeAsync(u, ct)));
    }

    /// <summary>The report for the registry, or null when there's nothing to say (no general URL blocked, nothing ran).</summary>
    public static StrategyReport? BuildReport(Config config, IReadOnlyList<string> urls, bool[] baseline,
        IReadOnlyDictionary<string, bool[]> results, IReadOnlyDictionary<string, string> desyncByName, IReadOnlySet<string> googleHosts)
    {
        var general = GeneralIndexes(urls, googleHosts).Take(MaxShared).ToList();
        if (general.Count == 0 || general.All(i => baseline[i]))
        {
            return null;
        }
        var shared = new List<StrategyResultOut>();
        foreach (var (name, r) in results)
        {
            if (!desyncByName.TryGetValue(name, out var desync) || ZapretDesync.Tokens(desync) is null || r.Length != urls.Count)
            {
                continue;
            }
            shared.Add(new StrategyResultOut { Args = desync, Passed = general.Count(i => r[i]), Total = general.Count });
        }
        if (shared.Count == 0)
        {
            return null;
        }
        var nic = NicInfo.Detect();
        return new StrategyReport
        {
            Client = BlockMapManager.Client,
            Engine = StrategyKey.Winws,
            Net = config.BlockMapItem.Net,
            Target = TargetFor(general.Select(i => urls[i])),
            // Detour stopped its own winws for the baseline and checked no other bypass runs.
            Env = new ProbeEnv { VpnAdapter = NicInfo.VpnAdapterUp(), DpiBypass = false, Nic = nic is not null },
            Baseline = new StrategyBaseline { Passed = general.Count(i => baseline[i]), Total = general.Count },
            Results = shared,
        };
    }

    public static async Task ShareAsync(StrategyReport report)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sent = await Registry.SendAsync(report, new Uri(BlockMapManager.Endpoint), NicInfo.Detect(), cts.Token);
            Logging.SaveLog(sent.Accepted
                ? $"{_tag}: shared {sent.Stored} strategies"
                : $"{_tag}: registry refused results ({sent.HttpStatus}): {sent.Error}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: sharing failed: {ex.Message}");
        }
    }

    /// <summary>The general profile's desync of a Flowseal strategy, normalized as the registry keys it; null if none.</summary>
    public static string? DesyncOf(string strategy, string? gameFilterMode)
    {
        try
        {
            var bat = Path.Combine(ZapretManager.ReleaseDir, strategy);
            var args = File.Exists(bat)
                ? ZapretStrategyParser.Parse(File.ReadAllText(bat), ZapretManager.BinDir, ZapretManager.ListsDir, gameFilterMode, out _)
                : null;
            var desync = args is null ? [] : ZapretDesync.Extract(args);
            var key = desync.Count > 0 ? StrategyKey.Normalize(StrategyKey.Winws, desync).Key : null;
            return key?[(key.IndexOf(':') + 1)..];
        }
        catch (IOException)
        {
            return null;
        }
    }
}
