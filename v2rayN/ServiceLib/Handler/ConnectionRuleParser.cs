namespace ServiceLib.Handler;

/// <summary>
/// Parses the short rule syntax used in the connection rules editor:
/// <code>
///   Discord.exe +zapret+vpn
///   youtube.com +zapret
///   chatgpt.com +vpn
///   1.2.3.0/24 +direct
///   @telegram +vpn
/// </code>
/// Modes can be written as one token ("+zapret+vpn") or separately ("+zapret +vpn"),
/// in English or Russian (+впн, +запрет, +напрямую, +блок).
/// </summary>
public static class ConnectionRuleParser
{
    public const string PresetPrefix = "@";

    private static readonly Dictionary<string, string> _modeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vpn"] = "vpn",
        ["впн"] = "vpn",
        ["proxy"] = "vpn",
        ["прокси"] = "vpn",
        ["zapret"] = "zapret",
        ["запрет"] = "zapret",
        ["dpi"] = "zapret",
        ["direct"] = "direct",
        ["напрямую"] = "direct",
        ["прямо"] = "direct",
        ["block"] = "block",
        ["блок"] = "block",
        ["блокировать"] = "block",
        ["warp"] = "warp",
        ["варп"] = "warp",
    };

    public static ConnectionRule? Parse(string? line, out string error)
    {
        error = string.Empty;
        if (line.IsNullOrEmpty() || line.TrimStart().StartsWith('#'))
        {
            error = "empty line";
            return null;
        }

        // Split "target +mode+mode" — the target may contain spaces (process paths), so the
        // modes are everything from the first " +" on.
        var text = line.Trim();
        var plusIdx = text.IndexOf('+');
        while (plusIdx > 0 && !char.IsWhiteSpace(text[plusIdx - 1]))
        {
            plusIdx = text.IndexOf('+', plusIdx + 1);
        }
        if (plusIdx <= 0)
        {
            error = "no mode given, add +vpn, +zapret, +zapret+vpn, +warp, +direct or +block";
            return null;
        }

        var target = NormalizeTarget(text[..plusIdx].Trim().Trim('"'));
        var modeWords = text[plusIdx..]
            .Split(['+', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var modes = new HashSet<string>();
        foreach (var w in modeWords)
        {
            if (!_modeAliases.TryGetValue(w, out var m))
            {
                error = $"unknown mode: +{w}";
                return null;
            }
            modes.Add(m);
        }

        var mode = ResolveMode(modes, out error);
        if (mode is null)
        {
            return null;
        }

        if (target.IsNullOrEmpty())
        {
            error = "no target given";
            return null;
        }
        if (GetTargetKind(target) == EConnectionTarget.Preset && ConnectionPresets.Find(target) is null)
        {
            error = $"unknown preset: {target}";
            return null;
        }

        return new ConnectionRule { Target = target, Mode = mode.Value };
    }

    private static EConnectionMode? ResolveMode(HashSet<string> modes, out string error)
    {
        error = string.Empty;
        if (modes.SetEquals(["vpn"]))
        {
            return EConnectionMode.Vpn;
        }
        if (modes.SetEquals(["zapret"]))
        {
            return EConnectionMode.Zapret;
        }
        if (modes.SetEquals(["zapret", "vpn"]))
        {
            return EConnectionMode.ZapretThenVpn;
        }
        if (modes.SetEquals(["direct"]))
        {
            return EConnectionMode.Direct;
        }
        if (modes.SetEquals(["block"]))
        {
            return EConnectionMode.Block;
        }
        if (modes.SetEquals(["warp"]))
        {
            return EConnectionMode.Warp;
        }
        error = $"modes cannot be combined: +{string.Join("+", modes)}";
        return null;
    }

    /// <summary>Formats a rule back into the editor syntax.</summary>
    public static string Format(ConnectionRule rule)
    {
        var mode = rule.Mode switch
        {
            EConnectionMode.Vpn => "+vpn",
            EConnectionMode.Zapret => "+zapret",
            EConnectionMode.ZapretThenVpn => "+zapret+vpn",
            EConnectionMode.Direct => "+direct",
            EConnectionMode.Block => "+block",
            EConnectionMode.Warp => "+warp",
            _ => "+vpn",
        };
        var target = rule.Target.Contains(' ') ? $"\"{rule.Target}\"" : rule.Target;
        return $"{target} {mode}";
    }

    public static EConnectionTarget GetTargetKind(string target)
    {
        if (target.StartsWith(PresetPrefix, StringComparison.Ordinal))
        {
            return EConnectionTarget.Preset;
        }
        if (target.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) || IsIpOrCidr(target))
        {
            return EConnectionTarget.Ip;
        }
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || target.Contains('\\')
            || target.Contains('/') && !IsIpOrCidr(target))
        {
            return EConnectionTarget.Process;
        }
        return EConnectionTarget.Domain;
    }

    public static bool IsIpOrCidr(string value)
    {
        var slash = value.IndexOf('/');
        var ipPart = slash >= 0 ? value[..slash] : value;
        if (!IPAddress.TryParse(ipPart, out var ip))
        {
            return false;
        }
        // Reject things like "1.2" that IPAddress.TryParse accepts as shorthand.
        if (ip.AddressFamily == AddressFamily.InterNetwork && ipPart.Count(c => c == '.') != 3)
        {
            return false;
        }
        if (slash < 0)
        {
            return true;
        }
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(value[(slash + 1)..], out var prefix) && prefix >= 0 && prefix <= max;
    }

    /// <summary>"*.example.com" and ".example.com" mean the same as "example.com" (domain and subdomains).</summary>
    private static string NormalizeTarget(string target)
    {
        if (target.StartsWith("*.", StringComparison.Ordinal))
        {
            target = target[2..];
        }
        else if (target.StartsWith('.') && !target.StartsWith(".\\"))
        {
            target = target[1..];
        }
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // People paste URLs; keep only the host.
            target = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : target;
        }
        return target.Trim();
    }
}
