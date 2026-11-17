namespace ServiceLib.Manager;

/// <summary>
/// Self-healing zapret. When the watchdog has kept "zapret then VPN" rules on the VPN for a while
/// (the ISP changed its DPI, the strategy stopped working), it picks a new strategy in the background;
/// if no strategy of the installed release passes everything, it installs a newer Flowseal release and
/// picks again. The watchdog then moves the rules back to zapret on its own (after two good checks).
/// </summary>
public static class ZapretHealer
{
    private const string _tag = "ZapretHealer";

    /// <summary>A rule must sit on the VPN this long before healing starts (short glitches are not worth it).</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    /// <summary>At most one healing round per this interval: each round restarts winws many times.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    public static DateTime? LastHeal { get; private set; }

    public static bool ShouldHeal(IEnumerable<ConnectionRule> rules, DateTime now, DateTime watchStarted, DateTime? lastHeal)
    {
        if (lastHeal is { } last && now - last < Cooldown)
        {
            return false;
        }
        // Rules put on the VPN at start have no FallbackSince: count from the watchdog start.
        return rules.Any(r => r.Enabled
                              && r.Mode == EConnectionMode.ZapretThenVpn
                              && r.FallbackActive
                              && ZapretWatchdog.ProbeUrls(r).Count > 0
                              && now - (r.FallbackSince ?? watchStarted) >= StuckAfter);
    }

    /// <summary>Newer release than the installed one? Tags are compared as versions when they parse.</summary>
    public static bool IsNewer(string? latest, string? installed)
    {
        if (latest.IsNullOrEmpty())
        {
            return false;
        }
        if (installed.IsNullOrEmpty())
        {
            return true;
        }
        static string Clean(string s) => new([.. s.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.')]);
        return Version.TryParse(Clean(latest), out var a) && Version.TryParse(Clean(installed), out var b)
            ? a > b
            : !latest.Equals(installed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One healing round. Returns the message for the user (null if there was nothing to do).</summary>
    public static async Task<string?> HealAsync(Config config, CancellationToken cancellationToken = default)
    {
        var zapret = ZapretManager.Instance;
        if (zapret.IsSelecting || ZapretManager.UsesTpws)
        {
            return null;
        }
        LastHeal = DateTime.Now;
        var before = (config.ZapretItem.Strategy, config.ZapretItem.Desync);
        Logging.SaveLog($"{_tag}: zapret→VPN rules stuck on VPN, picking a new strategy (now {before})");

        var (best, _) = await zapret.AutoSelectStrategyAsync(config, true, null, cancellationToken);
        if (best is null && zapret.IsSelecting)
        {
            return null;
        }

        string? updatedTo = null;
        if (best is null || best.Passed < best.Total)
        {
            // The installed release can't do it: Flowseal may already have strategies for the new DPI.
            using var downloadService = DownloadService.CreateDownloadServiceWithFallback();
            var latest = await zapret.GetLatestVersionAsync(downloadService, cancellationToken);
            var installed = config.ZapretItem.InstalledVersion ?? ZapretManager.BundledVersion;
            if (IsNewer(latest, installed))
            {
                Logging.SaveLog($"{_tag}: installing zapret {latest} (was {installed})");
                var (ok, message) = await zapret.InstallAsync(downloadService, latest, null, cancellationToken);
                if (ok)
                {
                    updatedTo = latest;
                    await ConnectionRulesManager.Instance.SyncZapretAsync(config);
                    var (best2, _) = await zapret.AutoSelectStrategyAsync(config, true, null, cancellationToken);
                    if (best2 != null && (best is null || best2.Passed >= best.Passed))
                    {
                        best = best2;
                    }
                }
                else
                {
                    Logging.SaveLog($"{_tag}: {message}");
                }
            }
        }

        var update = updatedTo is null ? string.Empty : string.Format(AppText.HealUpdated, updatedTo) + " ";
        if (best is not { Passed: > 0 })
        {
            return update + AppText.HealNothing;
        }
        var strategy = ZapretManager.DisplayStrategy(config.ZapretItem);
        var same = config.ZapretItem.Strategy.Equals(before.Strategy, StringComparison.OrdinalIgnoreCase)
                   && config.ZapretItem.Desync == before.Desync;
        return update + (same
            ? string.Format(AppText.HealKept, strategy, best.Passed, best.Total)
            : string.Format(AppText.HealPicked, strategy, best.Passed, best.Total));
    }
}
