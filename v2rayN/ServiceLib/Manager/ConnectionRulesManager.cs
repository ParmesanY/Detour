namespace ServiceLib.Manager;

/// <summary>
/// Applies the user's connection rules: core routing (via <see cref="ApplyToRouting" />) and
/// zapret lists + winws lifecycle (via <see cref="SyncZapretAsync" />).
/// </summary>
public class ConnectionRulesManager
{
    private static readonly Lazy<ConnectionRulesManager> _instance = new(() => new());
    public static ConnectionRulesManager Instance => _instance.Value;

    private const string _tag = "ConnectionRulesManager";
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CompiledConnectionRules Compile(Config config)
    {
        var compiled = ConnectionRuleCompiler.Compile(config.ConnectionRules, config.ConnectionRulesOverride, IsZapretExpected(config),
            // tpws not running: zapret rules go direct, as with winws off.
            ZapretManager.UsesTpws && ZapretManager.Instance.IsRunning ? Global.ZapretOutboundRemarks
            // Windows "TLS records" mode: the core cuts the handshake, winws keeps out (see SyncZapretAsync).
            : ZapretManager.SiteRecords(config.ZapretItem) != null ? Global.RecordsTag
            : Global.DirectTag);
        if (config.TgWsProxyItem?.Enabled == true)
        {
            compiled.RoutingRules.Insert(0, TgWsProxyManager.DirectRule());
        }
        return compiled;
    }

    /// <summary>zapret will be running alongside the cores: enabled, installed, admin rights (winws only).</summary>
    public static bool IsZapretExpected(Config config)
    {
        return config.ZapretItem?.Enabled == true
               && ZapretManager.Instance.IsInstalled
               && (ZapretManager.UsesTpws || Utils.IsAdministrator());
    }

    #region profiles

    /// <summary>Saves the current rules under <paramref name="name" />, replacing a profile with the same name.</summary>
    public static void SaveProfile(Config config, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }
        config.ConnectionRuleProfiles ??= [];
        config.ConnectionRuleProfiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        config.ConnectionRuleProfiles.Add(new ConnectionRuleProfile
        {
            Name = name,
            Rules = JsonUtils.DeepCopy(config.ConnectionRules ?? []) ?? [],
        });
    }

    /// <summary>Replaces the current rules with a copy of the profile's rules. False if not found.</summary>
    public static bool LoadProfile(Config config, string name)
    {
        var profile = config.ConnectionRuleProfiles?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            return false;
        }
        config.ConnectionRules = JsonUtils.DeepCopy(profile.Rules) ?? [];
        return true;
    }

    public static void DeleteProfile(Config config, string name)
    {
        config.ConnectionRuleProfiles?.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    #endregion profiles

    /// <summary>Routing item with the connection rules placed in front of its own rules.</summary>
    public RoutingItem? ApplyToRouting(Config config, RoutingItem? routing)
    {
        return ConnectionRuleCompiler.ApplyToRouting(routing, Compile(config));
    }

    /// <summary>
    /// Writes zapret lists from the rules and starts/restarts/stops winws as needed.
    /// Never throws; problems are logged and returned as a message.
    /// </summary>
    public async Task<string?> SyncZapretAsync(Config config)
    {
        if (ZapretManager.UsesTpws)
        {
            return await SyncTpwsAsync(config);
        }

        await _syncLock.WaitAsync();
        try
        {
            var compiled = Compile(config);
            foreach (var w in compiled.Warnings)
            {
                Logging.SaveLog($"{_tag}: {w}");
            }

            var zapret = ZapretManager.Instance;
            if (!zapret.IsInstalled)
            {
                return compiled.UsesZapret ? "zapret rules exist but zapret is not installed" : null;
            }

            // "TLS records" mode: the rules' sites are cut by the core; winws must leave them alone, or its desync
            // (positions found by parsing the ClientHello) lands on an already cut handshake.
            var records = ZapretManager.SiteRecords(config.ZapretItem) != null;
            var changed = ZapretManager.WriteUserLists(
                records ? [] : compiled.ZapretDomains,
                records ? [] : compiled.ZapretIps,
                records ? [.. compiled.ZapretExcludeDomains, .. compiled.ZapretDomains] : compiled.ZapretExcludeDomains,
                await ZapretManager.GetVpnServerIpsAsync());
            var shouldRun = config.ZapretItem.Enabled;

            if (!shouldRun)
            {
                if (zapret.IsRunning)
                {
                    await zapret.StopAsync();
                }
                return null;
            }

            if (zapret.IsRunning && !changed)
            {
                return null;
            }

            var (success, message) = await zapret.StartAsync();
            if (!success)
            {
                Logging.SaveLog($"{_tag}: {message}");
            }
            return success ? null : message;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return ex.Message;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// macOS/Linux: tpws needs no lists (the cores send it only "+zapret" traffic), just keep it
    /// running while zapret is on.
    /// </summary>
    private async Task<string?> SyncTpwsAsync(Config config)
    {
        await _syncLock.WaitAsync();
        try
        {
            var zapret = ZapretManager.Instance;
            if (!config.ZapretItem.Enabled || !zapret.IsInstalled)
            {
                if (zapret.IsRunning)
                {
                    await zapret.StopAsync();
                }
                return null;
            }
            if (zapret.IsRunning)
            {
                return null;
            }
            var (success, message) = await zapret.StartAsync();
            if (!success)
            {
                Logging.SaveLog($"{_tag}: {message}");
                return message;
            }
            // Zapret rules were built as "direct" while tpws was off.
            AppEvents.CoreReloadRequested.Publish();
            return null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return ex.Message;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    #region editing

    /// <summary>Parses editor text (one rule per line) and replaces the rule list. Returns per-line errors.</summary>
    public static List<string> ReplaceFromText(Config config, string text)
    {
        var errors = new List<string>();
        var rules = new List<ConnectionRule>();
        var old = config.ConnectionRules ?? [];
        var lineNo = 0;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // "# rule" is a switched-off rule; any other "#" line is a comment.
            var disabled = line.StartsWith('#');
            var rule = ConnectionRuleParser.Parse(disabled ? line.TrimStart('#').Trim() : line, out var error);
            if (rule is null)
            {
                if (!disabled)
                {
                    errors.Add($"{lineNo}: {error}");
                }
                continue;
            }
            rule.Enabled = !disabled;

            // Keep id, learned targets and zapret→VPN fallback state of an unchanged rule;
            // learned targets survive a mode change too.
            var sameTarget = old.FirstOrDefault(o => o.Target.Equals(rule.Target, StringComparison.OrdinalIgnoreCase));
            if (sameTarget != null)
            {
                rule.LearnedDomains = sameTarget.LearnedDomains;
                rule.LearnedIps = sameTarget.LearnedIps;
            }
            var same = old.FirstOrDefault(o => o.Target.Equals(rule.Target, StringComparison.OrdinalIgnoreCase) && o.Mode == rule.Mode);
            if (same == null && rule.Mode == EConnectionMode.ZapretThenVpn)
            {
                // A new "zapret then VPN" rule starts on VPN until zapret is verified for it.
                rule.FallbackActive = true;
            }
            if (same != null)
            {
                rule.Id = same.Id;
                rule.Remarks = same.Remarks;
                rule.FallbackActive = same.FallbackActive;
                rule.FallbackSince = same.FallbackSince;
                rule.FallbackCount = same.FallbackCount;
            }
            rules.Add(rule);
        }

        if (errors.Count == 0)
        {
            config.ConnectionRules = rules;
        }
        return errors;
    }

    public static string ToText(Config config)
    {
        return string.Join(Environment.NewLine, (config.ConnectionRules ?? [])
            .Select(r => (r.Enabled ? "" : "# ") + ConnectionRuleParser.Format(r)));
    }

    #endregion editing
}
