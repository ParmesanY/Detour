namespace ServiceLib.Manager;

/// <summary>
/// Kill switch: if the VPN core dies while the user is connected, outbound traffic is blocked by the
/// Windows Firewall (default outbound action = Block), except for the core programs, the client and
/// the local network, so nothing leaks unencrypted while the core reconnects. Released automatically
/// once the core runs again, or manually.
/// </summary>
public class KillSwitchManager
{
    private static readonly Lazy<KillSwitchManager> _instance = new(() => new());
    public static KillSwitchManager Instance => _instance.Value;

    private const string _tag = "KillSwitch";
    public const string RuleGroup = "vpn-client-killswitch";
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] _lanRanges =
        ["LocalSubnet", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "255.255.255.255"];

    private int _started;

    /// <summary>File holding the firewall state before engaging; its presence means "engaged".</summary>
    private static string StateFile => Utils.GetConfigPath("killswitch-state.json");

    public bool IsEngaged => File.Exists(StateFile);

    public void Start(Config config)
    {
        if (!Utils.IsWindows() || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        Task.Run(async () =>
        {
            // A block left over from a crash of the client itself: release it, the watcher below
            // engages again if the connection is still down.
            if (IsEngaged)
            {
                await ReleaseAsync();
            }

            using var timer = new PeriodicTimer(_pollInterval);
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(config);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
            }
        });
    }

    private async Task TickAsync(Config config)
    {
        var connected = config.TunModeItem.EnableTun
                        || config.SystemProxyItem.SysProxyType is ESysProxyType.ForcedChange or ESysProxyType.Pac;
        var coreDied = CoreManager.Instance.HasCoreDied;

        if (!IsEngaged && config.KillSwitchEnabled && connected && coreDied)
        {
            var error = await EngageAsync();
            NoticeManager.Instance.Enqueue(error ?? AppText.KillSwitchEngaged);
        }
        else if (IsEngaged && (!coreDied || !connected || !config.KillSwitchEnabled))
        {
            await ReleaseAsync();
            NoticeManager.Instance.Enqueue(AppText.KillSwitchReleased);
        }
    }

    /// <summary>Blocks outbound traffic. Returns an error message, or null on success.</summary>
    public async Task<string?> EngageAsync()
    {
        if (!Utils.IsAdministrator())
        {
            return AppText.KillSwitchNeedsAdmin;
        }

        var state = await RunPowerShellAsync(
            "Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultOutboundAction | ConvertTo-Json -Compress");
        if (state.ExitCode != 0 || state.Output.IsNullOrEmpty())
        {
            return AppText.KillSwitchFailed + " " + state.Output;
        }
        await File.WriteAllTextAsync(StateFile, state.Output);

        var result = await RunPowerShellAsync(BuildEngageScript(AllowedPrograms()));
        if (result.ExitCode != 0)
        {
            await ReleaseAsync();
            return AppText.KillSwitchFailed + " " + result.Output;
        }
        if (state.Output.Contains("\"Enabled\":0") || state.Output.Contains("\"Enabled\":false", StringComparison.OrdinalIgnoreCase))
        {
            Logging.SaveLog($"{_tag}: some firewall profiles are disabled, the block does not apply to them");
        }
        Logging.SaveLog($"{_tag}: engaged");
        return null;
    }

    /// <summary>Restores the firewall as it was before <see cref="EngageAsync" />.</summary>
    public async Task ReleaseAsync()
    {
        if (!IsEngaged)
        {
            return;
        }
        var saved = await File.ReadAllTextAsync(StateFile);
        var result = await RunPowerShellAsync(BuildReleaseScript(saved));
        if (result.ExitCode == 0)
        {
            File.Delete(StateFile);
            Logging.SaveLog($"{_tag}: released");
        }
        else
        {
            Logging.SaveLog($"{_tag}: release failed: {result.Output}");
        }
    }

    /// <summary>Core executables and the client itself may still reach the network to reconnect.</summary>
    private static List<string> AllowedPrograms()
    {
        var list = new List<string> { Utils.GetExePath() };
        foreach (var coreInfo in CoreInfoManager.Instance.GetCoreInfo())
        {
            var path = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
            if (path.IsNotEmpty() && File.Exists(path))
            {
                list.Add(path);
            }
        }
        // TG WS Proxy goes direct by design; the user turned it on for exactly that.
        if (AppManager.Instance.Config.TgWsProxyItem?.Enabled == true && File.Exists(TgWsProxyManager.ExePath))
        {
            list.Add(TgWsProxyManager.ExePath);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string BuildEngageScript(IEnumerable<string> programs)
    {
        var sb = new StringBuilder("$ErrorActionPreference='Stop'\n");
        sb.Append($"Remove-NetFirewallRule -Group '{RuleGroup}' -ErrorAction SilentlyContinue\n");
        foreach (var p in programs)
        {
            sb.Append($"New-NetFirewallRule -DisplayName 'vpn-client kill switch' -Group '{RuleGroup}' -Direction Outbound -Action Allow -Program {Quote(p)} | Out-Null\n");
        }
        sb.Append($"New-NetFirewallRule -DisplayName 'vpn-client kill switch (LAN)' -Group '{RuleGroup}' -Direction Outbound -Action Allow -RemoteAddress {string.Join(",", _lanRanges)} | Out-Null\n");
        sb.Append("Set-NetFirewallProfile -All -DefaultOutboundAction Block\n");
        return sb.ToString();
    }

    public static string BuildReleaseScript(string savedStateJson)
    {
        var sb = new StringBuilder();
        sb.Append($"Remove-NetFirewallRule -Group '{RuleGroup}' -ErrorAction SilentlyContinue\n");

        var node = JsonUtils.ParseJson(savedStateJson);
        var profiles = node is JsonArray arr ? arr.ToList() : [node];
        var restored = false;
        foreach (var p in profiles.OfType<JsonNode>())
        {
            var name = p["Name"]?.ToString();
            var action = OutboundActionName(p["DefaultOutboundAction"]);
            if (name.IsNotEmpty() && action != null)
            {
                sb.Append($"Set-NetFirewallProfile -Name {Quote(name!)} -DefaultOutboundAction {action}\n");
                restored = true;
            }
        }
        if (!restored)
        {
            // Unknown saved state: fall back to the Windows default.
            sb.Append("Set-NetFirewallProfile -All -DefaultOutboundAction Allow\n");
        }
        return sb.ToString();
    }

    /// <summary>ConvertTo-Json writes the enum as a number (0 NotConfigured, 1 Allow, 4 Block) or a name.</summary>
    private static string? OutboundActionName(JsonNode? value)
    {
        var text = value?.ToString();
        return text switch
        {
            "0" or "NotConfigured" => "NotConfigured",
            "1" or "Allow" => "Allow",
            "4" or "Block" => "Block",
            _ => null,
        };
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, ((await stdout) + (await stderr)).Trim());
    }
}
