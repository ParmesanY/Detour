namespace ServiceLib.Manager;

/// <summary>Result of checking one service: what works without the VPN and which rule fits.</summary>
/// <param name="Direct">Plain direct path: null = not checked.</param>
/// <param name="Zapret">Direct path with zapret: null = not checked (zapret off, or not needed).</param>
/// <param name="Current">The mode of the user's rule for this preset, null when there is none.</param>
public sealed record BlockCheckResult(
    ConnectionPreset Preset,
    bool? Direct,
    bool? Zapret,
    EConnectionMode Suggested,
    EConnectionMode? Current,
    string Verdict)
{
    /// <summary>Without a rule traffic already goes via VPN, so a VPN suggestion changes nothing.</summary>
    public bool ChangeSuggested => Current != Suggested && !(Current is null && Suggested == EConnectionMode.Vpn);
}

/// <summary>
/// "Check blocks": probes the popular services over the direct path (bound to the physical network card,
/// so the VPN tunnel doesn't carry it) with and without zapret, and suggests a rule for each:
/// works directly → direct, works with zapret → zapret→VPN, neither → VPN.
/// Services that refuse Russian users themselves are not probed: from a Russian IP they always need the VPN.
/// </summary>
public static class BlockCheckManager
{
    /// <summary>Services that answer, but refuse Russian addresses (a probe can't tell: data still flows).</summary>
    public static readonly IReadOnlySet<string> RefusesRussia =
        new HashSet<string>(["openai", "claude", "spotify", "gemini", "notion", "figma"]);

    /// <summary>Presets that aren't single services (or have no web host to probe).</summary>
    private static readonly HashSet<string> _skipped = ["russia", "torrent", "minecraft", "blocked"];

    private const int _parallel = 6;

    public static IEnumerable<ConnectionPreset> CheckedPresets =>
        ConnectionPresets.All.Where(p => !_skipped.Contains(p.Id));

    public static async Task<List<BlockCheckResult>> CheckAsync(Config config, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await CheckAsync(config, (url, viaZapret, ct) => ZapretWatchdog.ProbeAsync(url, viaZapret, ct, splitRecords: viaZapret), progress, cancellationToken);
    }

    /// <param name="probe">url, via zapret (tpws only) → data came through.</param>
    public static async Task<List<BlockCheckResult>> CheckAsync(Config config,
        Func<string, bool, CancellationToken, Task<bool>> probe, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var zapretRunning = ZapretManager.Instance.IsRunning;
        var presets = CheckedPresets.ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(_parallel);
        var tasks = presets.Select(async preset =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = CurrentMode(config, preset);
                var urls = ZapretWatchdog.ProbeUrls(new ConnectionRule { Target = preset.Target });
                var result = RefusesRussia.Contains(preset.Id) || urls.Count == 0
                    ? Evaluate(preset, current, null, null)
                    : await ProbePresetAsync(preset, current, urls, zapretRunning, probe, cancellationToken);
                progress?.Report(Interlocked.Increment(ref done) * 100 / presets.Count);
                return result;
            }
            finally
            {
                gate.Release();
            }
        });
        return [.. await Task.WhenAll(tasks)];
    }

    private static async Task<BlockCheckResult> ProbePresetAsync(ConnectionPreset preset, EConnectionMode? current,
        List<string> urls, bool zapretRunning, Func<string, bool, CancellationToken, Task<bool>> probe,
        CancellationToken ct)
    {
        async Task<bool> All(bool viaZapret) => (await Task.WhenAll(urls.Select(u => probe(u, viaZapret, ct)))).All(ok => ok);

        if (ZapretManager.UsesTpws)
        {
            // tpws is a separate proxy, so both paths can be checked independently.
            var direct = await All(false);
            bool? zapret = !direct && zapretRunning ? await All(true) : null;
            return Evaluate(preset, current, direct, zapret);
        }

        // Windows: winws handles every connection to the hosts in its list, so while it runs and this
        // service is in the list, the direct probe IS the zapret path.
        var inZapretList = zapretRunning && current is EConnectionMode.Zapret or EConnectionMode.ZapretThenVpn;
        var ok = await All(false);
        return inZapretList ? Evaluate(preset, current, null, ok) : Evaluate(preset, current, ok, null);
    }

    public static BlockCheckResult Evaluate(ConnectionPreset preset, EConnectionMode? current, bool? direct, bool? zapret)
    {
        // WARP is also a fine answer for ISP blocks; keep the user's WARP rule when the service needs a tunnel.
        var tunnel = current == EConnectionMode.Warp && !RefusesRussia.Contains(preset.Id) ? EConnectionMode.Warp : EConnectionMode.Vpn;
        var (mode, verdict) = (direct, zapret) switch
        {
            _ when RefusesRussia.Contains(preset.Id) => (EConnectionMode.Vpn, AppText.CheckRefusesRussia),
            (true, _) => (EConnectionMode.Direct, AppText.CheckWorksDirect),
            (_, true) => (EConnectionMode.ZapretThenVpn, AppText.CheckWorksZapret),
            (false, null) => (tunnel, AppText.CheckBlockedNoZapret),
            (_, false) => (tunnel, AppText.CheckBlocked),
            _ => (preset.RecommendedMode, AppText.CheckNotChecked),
        };
        // Direct where the user chose zapret: keep zapret, it only helps (and covers future throttling).
        if (mode == EConnectionMode.Direct && current is EConnectionMode.Zapret or EConnectionMode.ZapretThenVpn)
        {
            mode = current.Value;
        }
        return new(preset, direct, zapret, mode, current, verdict);
    }

    public static EConnectionMode? CurrentMode(Config config, ConnectionPreset preset)
    {
        return (config.ConnectionRules ?? [])
            .FirstOrDefault(r => r.Enabled && r.Target.Trim().Equals(preset.Target, StringComparison.OrdinalIgnoreCase))
            ?.Mode;
    }

    /// <summary>
    /// Puts the suggested modes into the rules text (the editor): replaces the line of an existing rule for
    /// the preset, or appends one. The user reviews and saves it as usual.
    /// </summary>
    public static string ApplyToText(string rulesText, IEnumerable<BlockCheckResult> results)
    {
        foreach (var r in results.Where(r => r.ChangeSuggested))
        {
            rulesText = SetRuleLine(rulesText, r.Preset.Target, r.Suggested);
        }
        return rulesText;
    }

    /// <summary>Replaces the line of the rule for <paramref name="target"/> or appends one.</summary>
    public static string SetRuleLine(string rulesText, string target, EConnectionMode mode)
    {
        var lines = rulesText.Replace("\r\n", "\n").Split('\n').ToList();
        var line = ConnectionRuleParser.Format(new ConnectionRule { Target = target, Mode = mode });
        var index = lines.FindIndex(l => l.Trim().StartsWith(target + " ", StringComparison.OrdinalIgnoreCase)
                                         || l.Trim().Equals(target, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            lines[index] = line;
        }
        else
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            lines.Add(line);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
