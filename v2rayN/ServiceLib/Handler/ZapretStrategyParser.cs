namespace ServiceLib.Handler;

/// <summary>
/// Turns a Flowseal zapret-discord-youtube strategy (.bat) into a winws.exe argument list,
/// so the client can launch winws itself instead of running the batch file.
/// </summary>
public static class ZapretStrategyParser
{
    public const string DefaultStrategy = "general.bat";
    public const string GameFilterDisabled = "disabled";

    /// <summary>File with IPs/subnets managed by the client; added next to every ipset-all.txt usage.</summary>
    public const string UserIpsetFile = "ipset-all-user.txt";

    // Flowseal uses port 12 as a "nothing" placeholder when the game filter is off.
    private const string GameFilterOffPort = "12";
    private const string GameFilterRange = "1024-65535";

    /// <summary>Strategy files in a release directory, default strategy first, then by name.</summary>
    public static List<string> ListStrategies(string releaseDir)
    {
        if (!Directory.Exists(releaseDir))
        {
            return [];
        }
        return SortStrategies(Directory.GetFiles(releaseDir, "general*.bat")
            .Select(Path.GetFileName)
            .OfType<string>());
    }

    /// <summary>
    /// general.bat, then ALT, ALT2 … ALT13 by number, then the FAKE families, unknown ones,
    /// and the experimental EXP last.
    /// </summary>
    public static List<string> SortStrategies(IEnumerable<string> names)
    {
        return names
            .Select(n => (Name: n, Key: SortKey(n)))
            .OrderBy(x => x.Key.Group)
            .ThenBy(x => x.Key.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Number)
            .Select(x => x.Name)
            .ToList();

        static (int Group, string Family, int Number) SortKey(string name)
        {
            var m = Regex.Match(name, @"\((?<inner>[^)]*)\)");
            var inner = m.Success ? m.Groups["inner"].Value.Trim() : string.Empty;
            if (inner.Length == 0)
            {
                return (0, string.Empty, 0);
            }

            // "FAKE TLS AUTO ALT2" → family "FAKE TLS AUTO ALT", number 2; "ALT" → number 1.
            var num = Regex.Match(inner, @"(\d+)$");
            var family = num.Success ? inner[..^num.Value.Length].Trim() : inner;
            var number = num.Success ? int.Parse(num.Value) : 1;
            var baseFamily = family.EndsWith("ALT", StringComparison.OrdinalIgnoreCase) && family.Length > 3
                ? family[..^3].Trim()
                : family;
            var isAltVariant = !family.Equals(baseFamily, StringComparison.OrdinalIgnoreCase);

            var group = family.Equals("ALT", StringComparison.OrdinalIgnoreCase) ? 1
                : inner.Equals("EXP", StringComparison.OrdinalIgnoreCase) || family.Equals("EXP", StringComparison.OrdinalIgnoreCase) ? 9
                : inner.Contains("FAKE", StringComparison.OrdinalIgnoreCase) ? 2
                : 3;
            // Inside a FAKE family the plain one comes before its ALT variants.
            return (group, baseFamily, group == 1 || isAltVariant ? number : 0);
        }
    }

    public static (string Tcp, string Udp) GetGameFilterPorts(string? mode)
    {
        return mode?.ToLowerInvariant() switch
        {
            "all" => (GameFilterRange, GameFilterRange),
            "tcp" => (GameFilterRange, GameFilterOffPort),
            "udp" => (GameFilterOffPort, GameFilterRange),
            _ => (GameFilterOffPort, GameFilterOffPort),
        };
    }

    /// <summary>
    /// Parses the batch file text. Returns null and an error message if the file has no
    /// winws.exe command or uses variables the client does not know how to fill in.
    /// </summary>
    public static List<string>? Parse(string batContent, string binDir, string listsDir, string? gameFilterMode, out string error)
    {
        error = string.Empty;
        var command = ExtractWinwsCommand(batContent);
        if (command is null)
        {
            error = "winws.exe command not found in strategy";
            return null;
        }

        var (tcp, udp) = GetGameFilterPorts(gameFilterMode);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = WithTrailingSlash(binDir),
            ["LISTS"] = WithTrailingSlash(listsDir),
            ["GameFilter"] = tcp != GameFilterOffPort ? tcp : udp,
            ["GameFilterTCP"] = tcp,
            ["GameFilterUDP"] = udp,
        };

        var expanded = Regex.Replace(command, "%([A-Za-z0-9_]+)%",
            m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        var args = new List<string>();
        foreach (var token in Tokenize(expanded))
        {
            if (Regex.IsMatch(token, "%[A-Za-z0-9_~]+%?"))
            {
                error = $"unsupported variable in strategy argument: {token}";
                return null;
            }
            args.Add(token);

            // Let the client's own IP list work alongside the release's ipset-all.txt.
            if (token.StartsWith("--ipset=", StringComparison.Ordinal)
                && token.EndsWith("ipset-all.txt", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--ipset=" + Path.Combine(listsDir, UserIpsetFile));
            }
        }

        if (args.Count == 0)
        {
            error = "strategy has no winws arguments";
            return null;
        }
        return args;
    }

    /// <summary>Joins the "^"-continued lines of the winws command and returns the text after "winws.exe".</summary>
    internal static string? ExtractWinwsCommand(string batContent)
    {
        var lines = batContent.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var capturing = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (!capturing)
            {
                var idx = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                if (idx < 0 || line.TrimStart().StartsWith("::") || line.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                capturing = true;
                line = line[(idx + "winws.exe".Length)..].TrimStart('"');
            }

            var continues = line.EndsWith('^');
            if (continues)
            {
                line = line[..^1];
            }
            sb.Append(line).Append(' ');
            if (!continues)
            {
                break;
            }
        }

        return capturing ? sb.ToString().Trim() : null;
    }

    /// <summary>
    /// Splits on whitespace outside double quotes and drops the quotes. Outside quotes "^" escapes
    /// the next character, as in cmd: "--dpi-desync-fake-tls=^!" reaches winws as "...=!".
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var escaped = false;
        foreach (var ch in text)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '^' && !inQuotes)
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    private static string WithTrailingSlash(string dir)
    {
        return dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
    }
}
