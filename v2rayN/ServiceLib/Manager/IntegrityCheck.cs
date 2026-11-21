namespace ServiceLib.Manager;

/// <summary>
/// Windows Defender and other antiviruses quarantine WinDivert, winws and sometimes the cores (false positives:
/// WinDivert is a signed packet driver). To the user it looked like "core failed to start" or a connection that
/// never comes up. Detour remembers which of these files it had (guiConfigs/integrity.json); when one it had is
/// gone, that's almost always the antivirus, and the user gets a plain explanation with two fixes: exclude the
/// bin folder in Defender (UAC, only on click) and download the files again.
/// </summary>
public static class IntegrityCheck
{
    private const string _tag = "IntegrityCheck";
    private static readonly Lock _gate = new();
    private static string? _lastReported;

    private static string StatePath => Utils.GetConfigPath("integrity.json");

    /// <summary>The files worth watching that exist on this install right now.</summary>
    public static List<string> Watched()
    {
        var list = new List<string>();
        foreach (var core in new[] { ECoreType.Xray, ECoreType.sing_box })
        {
            var exe = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(core), out _);
            if (exe.IsNotEmpty())
            {
                list.Add(exe);
            }
        }
        if (Utils.IsWindows())
        {
            list.AddRange(new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" }
                .Select(f => Path.Combine(ZapretManager.BinDir, f))
                .Where(File.Exists));
            if (File.Exists(TgWsProxyManager.ExePath))
            {
                list.Add(TgWsProxyManager.ExePath);
            }
            // Tor and lyrebird (emergency mode) are Go/C binaries antiviruses like to flag too.
            list.AddRange(new[] { TorManager.ExePath, TorManager.TransportPath }.Where(File.Exists));
        }
        return list;
    }

    /// <summary>
    /// Pure core of the check: the remembered files that are gone, and the new remembered set (gone ones stay,
    /// so the warning keeps coming until they are back).
    /// </summary>
    public static (List<string> Missing, List<string> Remember) Compare(IEnumerable<string> remembered, IEnumerable<string> present, Func<string, bool> exists)
    {
        var known = remembered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = known.Where(p => !exists(p)).ToList();
        var remember = known.Concat(present).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (missing, remember);
    }

    /// <summary>Checks, remembers what's there now, and reports newly missing files once per session.</summary>
    public static List<string> Run()
    {
        try
        {
            lock (_gate)
            {
                var remembered = File.Exists(StatePath)
                    ? JsonUtils.Deserialize<List<string>>(File.ReadAllText(StatePath)) ?? []
                    : [];
                var (missing, remember) = Compare(remembered, Watched(), File.Exists);
                File.WriteAllText(StatePath, JsonUtils.Serialize(remember));
                if (missing.Count > 0)
                {
                    var key = string.Join("|", missing);
                    if (key != _lastReported)
                    {
                        _lastReported = key;
                        Logging.SaveLog($"{_tag}: missing (antivirus?): {string.Join(", ", missing)}");
                        AppEvents.FilesRemovedDetected.Publish(missing);
                    }
                }
                return missing;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.SaveLog(_tag, ex);
            return [];
        }
    }

    /// <summary>Forget a file that is gone on purpose (or after a reinstall into another place).</summary>
    public static void Forget(IEnumerable<string> files)
    {
        lock (_gate)
        {
            var remembered = File.Exists(StatePath) ? JsonUtils.Deserialize<List<string>>(File.ReadAllText(StatePath)) ?? [] : [];
            var drop = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(StatePath, JsonUtils.Serialize(remembered.Where(f => !drop.Contains(f)).ToList()));
            _lastReported = null;
        }
    }

    /// <summary>The PowerShell that excludes the folder in Windows Defender.</summary>
    public static string ExclusionScript(string dir) =>
        "$ErrorActionPreference='Stop'\n"
        + "if (-not (Get-Command Add-MpPreference -ErrorAction SilentlyContinue)) { Write-Output 'no-defender'; exit 3 }\n"
        + $"Add-MpPreference -ExclusionPath {KillSwitchManager.Quote(dir)}\n";

    /// <summary>
    /// Adds Detour's bin folder to Defender's exclusions. Only ever called from the user's click; asks for admin
    /// rights through UAC when Detour runs without them. Returns an error message or null.
    /// </summary>
    public static async Task<string?> AddDefenderExclusionAsync()
    {
        if (!Utils.IsWindows())
        {
            return AppText.IntegrityNoDefender;
        }
        var dir = Utils.GetBinPath("");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ExclusionScript(dir)));
        if (Utils.IsAdministrator())
        {
            psi.UseShellExecute = false;
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
            {
                psi.ArgumentList.Add(a);
            }
        }
        else
        {
            // runas needs ShellExecute, which takes one argument string; the script itself is base64, no quoting issues.
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";
        }
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return AppText.IntegrityExclusionFailed;
            }
            await p.WaitForExitAsync();
            Logging.SaveLog($"{_tag}: Defender exclusion for {dir}: exit {p.ExitCode}");
            return p.ExitCode switch
            {
                0 => null,
                3 => AppText.IntegrityNoDefender,
                _ => AppText.IntegrityExclusionFailed,
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC prompt declined.
            return AppText.IntegrityExclusionDeclined;
        }
    }

    /// <summary>
    /// Downloads what went missing again: zapret through its installer; the cores are left to "Check updates"
    /// (their update path asks the user anyway). Returns a message for the user.
    /// </summary>
    public static async Task<string> RestoreAsync(IReadOnlyList<string> missing)
    {
        var zapretGone = missing.Any(m => m.StartsWith(ZapretManager.BinDir, StringComparison.OrdinalIgnoreCase));
        var tgGone = missing.Any(m => m.Equals(TgWsProxyManager.ExePath, StringComparison.OrdinalIgnoreCase));
        var coresGone = missing.Any(m => !m.StartsWith(ZapretManager.BinDir, StringComparison.OrdinalIgnoreCase)
                                         && !m.Equals(TgWsProxyManager.ExePath, StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>();
        using var downloadService = new DownloadService();
        if (zapretGone)
        {
            var (ok, msg) = await ZapretManager.Instance.InstallAsync(downloadService, AppManager.Instance.Config.ZapretItem.InstalledVersion.NullIfEmpty());
            notes.Add(ok ? AppText.IntegrityZapretRestored : $"zapret: {msg}");
        }
        if (tgGone)
        {
            var (ok, msg) = await TgWsProxyManager.Instance.InstallAsync(downloadService, AppManager.Instance.Config.TgWsProxyItem.InstalledVersion.NullIfEmpty());
            notes.Add(ok ? "TG WS Proxy ✓" : $"TG WS Proxy: {msg}");
        }
        if (coresGone)
        {
            notes.Add(AppText.IntegrityCoresHint);
        }
        Run();
        return string.Join("\n", notes);
    }
}
