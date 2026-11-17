namespace ServiceLib.Handler;

/// <summary>
/// A strategy from the Detour Map registry is only the desync part of one winws profile (TCP 443, checked by
/// detour-probe tune). Detour lays it over the general profile of the chosen Flowseal strategy (the one with
/// list-general.txt) and keeps the rest of the .bat as is: QUIC, Discord voice, the Google list, games.
/// </summary>
public static partial class ZapretDesync
{
    /// <summary>Prefix of registry candidates in the strategy picker's list ("registry:&lt;id&gt;").</summary>
    public const string RegistryPrefix = "registry:";

    [GeneratedRegex(@"^--(dpi-desync(-[a-z0-9]+)*|ip-id|dup(-[a-z0-9]+)*|orig(-[a-z0-9]+)*)(=|$)")]
    private static partial Regex DesyncOption();

    public static bool IsDesyncOption(string token) => DesyncOption().IsMatch(token);

    /// <summary>
    /// [start, end) of the general profile: profiles are separated by "--new"; the general one filters TCP 443
    /// and reads list-general.txt. Null when the strategy has no such profile.
    /// </summary>
    public static (int Start, int End)? GeneralProfile(IReadOnlyList<string> args)
    {
        var start = 0;
        for (var i = 0; i <= args.Count; i++)
        {
            if (i < args.Count && args[i] != "--new")
            {
                continue;
            }
            var profile = args.Skip(start).Take(i - start).ToList();
            var tcp443 = profile.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal)
                                          && a["--filter-tcp=".Length..].Split(',').Contains("443"));
            var general = profile.Any(a => a.StartsWith("--hostlist=", StringComparison.Ordinal)
                                           && a.EndsWith("list-general.txt", StringComparison.OrdinalIgnoreCase));
            if (tcp443 && general)
            {
                return (start, i);
            }
            start = i + 1;
        }
        return null;
    }

    /// <summary>
    /// The args with the general profile's desync options replaced by <paramref name="desync" />
    /// (<c>%BIN%name.bin</c> resolved against <paramref name="binDir" />). Null when there is no general profile.
    /// </summary>
    public static List<string>? Apply(IReadOnlyList<string> args, IEnumerable<string> desync, string binDir)
    {
        if (GeneralProfile(args) is not var (start, end))
        {
            return null;
        }
        var bin = binDir.EndsWith(Path.DirectorySeparatorChar) ? binDir : binDir + Path.DirectorySeparatorChar;
        var profile = args.Skip(start).Take(end - start).Where(a => !IsDesyncOption(a)).ToList();
        profile.AddRange(desync.Select(a => a.Replace(Detour.NetProbe.TuneCandidate.BinToken, bin, StringComparison.Ordinal)));
        return [.. args.Take(start), .. profile, .. args.Skip(end)];
    }

    /// <summary>The general profile's desync options (what a strategy does to the sites Detour checks), or none.</summary>
    public static List<string> Extract(IReadOnlyList<string> args)
    {
        return GeneralProfile(args) is var (start, end)
            ? [.. args.Skip(start).Take(end - start).Where(IsDesyncOption)]
            : [];
    }

    /// <summary>Registry args as winws tokens, or null when they don't pass the registry's own allowlist.</summary>
    public static string[]? Tokens(string? desync)
    {
        if (desync.IsNullOrEmpty())
        {
            return null;
        }
        var normalized = Detour.NetProbe.StrategyKey.Normalize(Detour.NetProbe.StrategyKey.Winws, desync!);
        return normalized.Key is null ? null : normalized.Tokens;
    }
}
