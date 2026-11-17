namespace ServiceLib.Handler;

/// <summary>Result of compiling the user's connection rules.</summary>
public class CompiledConnectionRules
{
    /// <summary>Core routing rules, to be placed before the regular routing rules.</summary>
    public List<RulesItem> RoutingRules { get; } = [];

    /// <summary>Domains for zapret's hostlist (list-general-user.txt).</summary>
    public List<string> ZapretDomains { get; } = [];

    /// <summary>IPs/subnets for zapret's ipset (ipset-all-user.txt).</summary>
    public List<string> ZapretIps { get; } = [];

    /// <summary>Domains the user explicitly sends elsewhere; excluded from zapret (list-exclude-user.txt).</summary>
    public List<string> ZapretExcludeDomains { get; } = [];

    public List<string> Warnings { get; } = [];

    public bool UsesZapret => ZapretDomains.Count > 0 || ZapretIps.Count > 0;
}

/// <summary>
/// Turns connection rules into core routing rules and zapret lists.
/// Rules are evaluated top to bottom, first match wins (same as the cores).
/// </summary>
public static class ConnectionRuleCompiler
{
    public const string RemarksPrefix = "[rules] ";

    /// <summary>The mode actually in effect, taking an active zapret→VPN fallback into account.</summary>
    public static EConnectionMode EffectiveMode(ConnectionRule rule)
    {
        return rule.Mode == EConnectionMode.ZapretThenVpn
            ? rule.FallbackActive ? EConnectionMode.Vpn : EConnectionMode.Zapret
            : rule.Mode;
    }

    /// <param name="zapretAvailable">
    /// False when zapret is off, not installed or cannot run: "zapret then VPN" rules go straight to
    /// VPN instead of going direct with nothing to bypass DPI.
    /// </param>
    /// <param name="zapretOutbound">
    /// Where zapret traffic goes: direct on Windows (winws catches it on the network card), the tpws
    /// SOCKS proxy (<see cref="Global.ZapretOutboundRemarks" />) on macOS/Linux.
    /// </param>
    public static CompiledConnectionRules Compile(IEnumerable<ConnectionRule>? rules, EConnectionMode? overrideMode = null,
        bool zapretAvailable = true, string zapretOutbound = Global.DirectTag)
    {
        var result = new CompiledConnectionRules();
        if (overrideMode == EConnectionMode.ZapretThenVpn)
        {
            overrideMode = EConnectionMode.Zapret;
        }
        foreach (var rule in rules ?? [])
        {
            if (!rule.Enabled || rule.Target.IsNullOrEmpty())
            {
                continue;
            }

            var (domains, ips, processes) = Expand(rule, result.Warnings);
            if (domains.Count + ips.Count + processes.Count == 0)
            {
                continue;
            }

            var mode = overrideMode is { } forced && rule.Mode != EConnectionMode.Block ? forced : EffectiveMode(rule);
            if (!zapretAvailable && rule.Mode == EConnectionMode.ZapretThenVpn && mode == EConnectionMode.Zapret)
            {
                mode = EConnectionMode.Vpn;
            }
            var outbound = mode switch
            {
                // A rule with its own server: resolved by CoreConfigContextBuilder (ServiceServerResolver).
                EConnectionMode.Vpn => rule.Server.IsNullOrEmpty() ? Global.ProxyTag : ServiceServerResolver.TagPrefix + rule.Server,
                EConnectionMode.Block => Global.BlockTag,
                // Resolved by CoreConfigContextBuilder to the WARP local proxy (or the VPN while it's off).
                EConnectionMode.Warp => Global.WarpOutboundRemarks,
                EConnectionMode.Zapret => zapretOutbound,
                _ => Global.DirectTag,
            };
            var remarks = RemarksPrefix + (rule.Remarks.IsNullOrEmpty() ? rule.Target : rule.Remarks);

            // zapret targets must not be resolved by the ISP's DNS, which returns stub addresses for
            // blocked domains. Routing-only rules leave their DNS on the regular (remote) resolver.
            var ruleType = mode == EConnectionMode.Zapret ? ERuleType.Routing : ERuleType.ALL;

            // Separate core rules per kind: fields inside one rule are AND-ed by the cores.
            if (processes.Count > 0)
            {
                result.RoutingRules.Add(NewRule(remarks, outbound, ruleType, process: processes));
            }
            if (domains.Count > 0)
            {
                var coreDomains = domains.Select(ToCoreDomain).ToList();
                if (mode == EConnectionMode.Zapret)
                {
                    // Block QUIC (UDP 443) for zapret domains: ISPs often drop it outright, and browsers
                    // then hang on it instead of using TCP, where zapret works reliably.
                    var quic = NewRule(remarks + " (QUIC)", Global.BlockTag, ruleType, domain: coreDomains);
                    quic.Network = "udp";
                    quic.Port = "443";
                    result.RoutingRules.Add(quic);
                }
                result.RoutingRules.Add(NewRule(remarks, outbound, ruleType, domain: coreDomains));
            }
            if (ips.Count > 0)
            {
                result.RoutingRules.Add(NewRule(remarks, outbound, ruleType, ip: ips));
            }

            // "zapret then VPN" targets stay in zapret's lists even while they route through the VPN:
            // the watchdog checks them directly to decide when to switch back, and an excluded domain
            // would never pass that check. Tunnelled traffic is not affected (winws sees only the
            // encrypted packets to the VPN server, whose IPs are excluded).
            if (mode == EConnectionMode.Zapret || rule.Mode == EConnectionMode.ZapretThenVpn && overrideMode is null && zapretAvailable)
            {
                AddZapretTargets(rule, domains, ips, processes, result);
            }
            else if (mode is EConnectionMode.Vpn or EConnectionMode.Block or EConnectionMode.Warp)
            {
                // Keep zapret's hands off traffic the user sent elsewhere if it ever goes direct.
                result.ZapretExcludeDomains.AddRange(domains.Where(IsPlainDomain));
            }
        }

        Dedupe(result.ZapretDomains);
        Dedupe(result.ZapretIps);
        Dedupe(result.ZapretExcludeDomains);
        // A domain can't be both processed and excluded; an earlier zapret rule wins.
        result.ZapretExcludeDomains.RemoveAll(d => result.ZapretDomains.Contains(d, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Returns a copy of <paramref name="routing" /> with the compiled rules placed first.
    /// The original item (a database entity) is not modified.
    /// </summary>
    public static RoutingItem? ApplyToRouting(RoutingItem? routing, CompiledConnectionRules compiled)
    {
        if (routing is null || compiled.RoutingRules.Count == 0)
        {
            return routing;
        }
        var copy = JsonUtils.DeepCopy(routing) ?? routing;
        var existing = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
        copy.RuleSet = JsonUtils.Serialize(compiled.RoutingRules.Concat(existing).ToList(), false);
        return copy;
    }

    private static (List<string> Domains, List<string> Ips, List<string> Processes) Expand(ConnectionRule rule, List<string> warnings)
    {
        var target = rule.Target.Trim();
        switch (ConnectionRuleParser.GetTargetKind(target))
        {
            case EConnectionTarget.Preset:
                var preset = ConnectionPresets.Find(target);
                if (preset is null)
                {
                    warnings.Add($"unknown preset {target}");
                    return ([], [], []);
                }
                return ([.. preset.Domains], [.. preset.Ips], [.. preset.Processes]);

            case EConnectionTarget.Ip:
                return ([], [target], []);

            case EConnectionTarget.Process:
                return ([.. rule.LearnedDomains ?? []], [.. rule.LearnedIps ?? []], [target]);

            default:
                return ([target], [], []);
        }
    }

    private static void AddZapretTargets(ConnectionRule rule, List<string> domains, List<string> ips, List<string> processes,
        CompiledConnectionRules result)
    {
        result.ZapretDomains.AddRange(domains.Where(IsPlainDomain));
        result.ZapretIps.AddRange(ips.Where(ConnectionRuleParser.IsIpOrCidr));

        if (domains.Any(d => !IsPlainDomain(d)) || ips.Any(i => !ConnectionRuleParser.IsIpOrCidr(i)))
        {
            result.Warnings.Add($"{rule.Target}: geosite/geoip lists can't be passed to zapret, only plain domains and IPs");
        }

        // zapret works on packets and doesn't know processes. A process rule only makes the
        // traffic go direct; its domains must reach zapret through a preset or learning mode.
        if (processes.Count > 0 && domains.Count == 0 && ips.Count == 0)
        {
            result.Warnings.Add($"{rule.Target}: zapret needs the app's domains; use a preset or learning mode, for now the app only goes direct");
        }
    }

    /// <summary>Plain domains become "domain:" (domain and subdomains); geosite:/full:/keyword:/regexp: stay as is.</summary>
    private static string ToCoreDomain(string domain)
    {
        return IsPlainDomain(domain) ? "domain:" + domain : domain;
    }

    private static bool IsPlainDomain(string value)
    {
        return !value.Contains(':') && !value.Contains('/') && !value.Contains('*');
    }

    private static RulesItem NewRule(string remarks, string outbound, ERuleType ruleType, List<string>? domain = null,
        List<string>? ip = null, List<string>? process = null)
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            Remarks = remarks,
            OutboundTag = outbound,
            Domain = domain,
            Ip = ip,
            Process = process,
            Enabled = true,
            RuleType = ruleType,
        };
    }

    private static void Dedupe(List<string> list)
    {
        var distinct = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.Clear();
        list.AddRange(distinct);
    }
}
