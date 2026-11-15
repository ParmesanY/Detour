using System.IO.Compression;

namespace ServiceLib.Manager;

/// <summary>
/// Installs, updates and runs zapret (Flowseal zapret-discord-youtube build, Windows only).
/// The release is unpacked into bin/zapret/current; winws.exe is started directly with
/// arguments parsed from the selected strategy .bat.
/// </summary>
public partial class ZapretManager
{
    private static readonly Lazy<ZapretManager> _instance = new(() => new());
    public static ZapretManager Instance => _instance.Value;

    private const string _tag = "ZapretManager";
    public const string RepoUrl = "https://github.com/Flowseal/zapret-discord-youtube";
    private const string WinwsExe = "winws.exe";

    // Flowseal's own user lists; it also refuses empty list files, hence the placeholders.
    public const string UserHostlistFile = "list-general-user.txt";
    public const string UserExcludeFile = "list-exclude-user.txt";
    public const string UserIpsetExcludeFile = "ipset-exclude-user.txt";
    private const string PlaceholderDomain = "domain.example.abc";
    private const string PlaceholderIp = "203.0.113.113/32";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private Process? _process;

    /// <summary>Raised after winws was started, stopped or exited on its own (any thread).</summary>
    public event Action? StateChanged;
    private WindowsJobService? _job;

    public static string RootDir => Utils.GetBinPath("", "zapret");
    public static string ReleaseDir => Path.Combine(RootDir, "current");
    private static string PreviousDir => Path.Combine(RootDir, "previous");
    public static string BinDir => Path.Combine(ReleaseDir, "bin");
    public static string ListsDir => Path.Combine(ReleaseDir, "lists");

    public bool IsInstalled => UsesTpws ? File.Exists(TpwsExePath) : File.Exists(Path.Combine(BinDir, WinwsExe));

    /// <summary>Version of the release shipped in the zip (tools/package-release.ps1 writes current/.version).</summary>
    public static string? BundledVersion
    {
        get
        {
            try
            {
                var file = UsesTpws ? Path.Combine(TpwsDir, ".version") : Path.Combine(ReleaseDir, ".version");
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool IsRunning => _process is { HasExited: false };

    public List<string> GetStrategies() => UsesTpws ? [.. TpwsStrategies.Select(s => s.Name)] : ZapretStrategyParser.ListStrategies(ReleaseDir);

    #region install / update

    /// <summary>Latest release tag, resolved through the github.com "latest" redirect (no API token needed).</summary>
    public async Task<string?> GetLatestVersionAsync(DownloadService downloadService, CancellationToken cancellationToken = default)
    {
        var location = await downloadService.ResolveFinalUrlAsync($"{RepoUrl}/releases/latest", cancellationToken);
        return ParseReleaseTag(location);
    }

    /// <summary>".../releases/tag/1.10.4" → "1.10.4"; null if the URL has no tag.</summary>
    public static string? ParseReleaseTag(string? url)
    {
        if (url is null || !url.Contains("/tag/"))
        {
            return null;
        }
        var tag = url.Split("/tag/").Last().Split('?', '#')[0].Trim('/');
        return tag.IsNullOrEmpty() ? null : Uri.UnescapeDataString(tag);
    }

    /// <summary>
    /// Downloads the release zip and swaps it in. The previous release is kept in bin/zapret/previous
    /// and the client's user lists are carried over.
    /// </summary>
    public async Task<(bool Success, string Message)> InstallAsync(DownloadService downloadService, string? version = null,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        if (UsesTpws)
        {
            return await InstallTpwsAsync(downloadService, version, progress, cancellationToken);
        }

        await _lock.WaitAsync(cancellationToken);
        var zipPath = Utils.GetTempPath($"zapret-{Utils.GetGuid()}.zip");
        var stagingDir = Path.Combine(RootDir, "staging");
        try
        {
            version ??= await GetLatestVersionAsync(downloadService, cancellationToken);
            if (version.IsNullOrEmpty())
            {
                return (false, "could not resolve the latest zapret release (GitHub unreachable?)");
            }

            var url = $"{RepoUrl}/releases/download/{version}/zapret-discord-youtube-{version}.zip";
            await Report(progress, $"Downloading zapret {version}");
            string? downloadError = null;
            downloadService.Error += (_, e) => downloadError ??= e.GetException().Message;
            await downloadService.DownloadFileAsync(new() { FileUrl = url, FilePath = zipPath }, cancellationToken);
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                return (false, $"download failed: {downloadError ?? url}");
            }

            await Report(progress, "Unpacking");
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            var releaseRoot = FindReleaseRoot(stagingDir);
            if (releaseRoot is null)
            {
                return (false, $"{WinwsExe} not found in the downloaded archive");
            }

            var wasRunning = IsRunning;
            await StopAsync();

            CarryOverUserLists(Path.Combine(ReleaseDir, "lists"), Path.Combine(releaseRoot, "lists"));
            if (Directory.Exists(PreviousDir))
            {
                Directory.Delete(PreviousDir, true);
            }
            if (Directory.Exists(ReleaseDir))
            {
                Directory.Move(ReleaseDir, PreviousDir);
            }
            Directory.Move(releaseRoot, ReleaseDir);
            EnsureUserLists();

            var config = AppManager.Instance.Config;
            config.ZapretItem.InstalledVersion = version;
            if (!GetStrategies().Contains(config.ZapretItem.Strategy, StringComparer.OrdinalIgnoreCase))
            {
                config.ZapretItem.Strategy = ZapretStrategyParser.DefaultStrategy;
            }
            await ConfigHandler.SaveConfig(config);

            if (wasRunning)
            {
                await StartAsync();
            }
            return (true, $"zapret {version} installed");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, true);
                }
            }
            catch { }
            _lock.Release();
        }
    }

    /// <summary>The release zip may or may not have a top-level folder; find the one holding bin/winws.exe.</summary>
    private static string? FindReleaseRoot(string dir)
    {
        var winws = Directory.GetFiles(dir, WinwsExe, SearchOption.AllDirectories).FirstOrDefault();
        var binDir = winws is null ? null : Path.GetDirectoryName(winws);
        return binDir is null ? null : Path.GetDirectoryName(binDir);
    }

    private static void CarryOverUserLists(string fromDir, string toDir)
    {
        if (!Directory.Exists(fromDir))
        {
            return;
        }
        Directory.CreateDirectory(toDir);
        foreach (var name in new[] { UserHostlistFile, UserExcludeFile, UserIpsetExcludeFile, ZapretStrategyParser.UserIpsetFile })
        {
            var src = Path.Combine(fromDir, name);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(toDir, name), true);
            }
        }
    }

    #endregion install / update

    #region user lists

    /// <summary>Creates missing user list files with Flowseal-style placeholders.</summary>
    public static void EnsureUserLists()
    {
        if (!Directory.Exists(ListsDir))
        {
            return;
        }
        WriteIfMissing(UserHostlistFile, PlaceholderDomain);
        WriteIfMissing(UserExcludeFile, PlaceholderDomain);
        WriteIfMissing(UserIpsetExcludeFile, PlaceholderIp);
        WriteIfMissing(ZapretStrategyParser.UserIpsetFile, PlaceholderIp);

        static void WriteIfMissing(string name, string placeholder)
        {
            var path = Path.Combine(ListsDir, name);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, placeholder + Environment.NewLine);
            }
        }
    }

    /// <summary>
    /// Replaces the client-managed lists. Takes effect on the next (re)start of winws.
    /// Returns true if any file content changed.
    /// </summary>
    public static bool WriteUserLists(IEnumerable<string> domains, IEnumerable<string> ips, IEnumerable<string> excludeDomains,
        IEnumerable<string>? excludeIps = null)
    {
        if (!Directory.Exists(ListsDir))
        {
            return false;
        }
        var changed = false;
        changed |= WriteList(UserHostlistFile, domains, PlaceholderDomain);
        changed |= WriteList(ZapretStrategyParser.UserIpsetFile, ips, PlaceholderIp);
        changed |= WriteList(UserExcludeFile, excludeDomains, PlaceholderDomain);
        if (excludeIps != null)
        {
            changed |= WriteList(UserIpsetExcludeFile, excludeIps, PlaceholderIp);
        }
        return changed;
    }

    /// <summary>
    ///     IPs of all VPN servers in the profile list. zapret must never touch traffic to them:
    ///     the strategies match on SNI, and a Reality key whose SNI is e.g. www.google.com would
    ///     get its handshake split/faked by winws and stop working.
    /// </summary>
    public static async Task<List<string>> GetVpnServerIpsAsync()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var profiles = await AppManager.Instance.ProfileItems("") ?? [];
            var hosts = profiles
                .Where(p => p.ConfigType != EConfigType.Custom && p.Address.IsNotEmpty())
                .Select(p => p.Address.Trim().Trim('[', ']'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (host, _) =>
            {
                if (IPAddress.TryParse(host, out var ip))
                {
                    lock (result)
                    {
                        result.Add(ToCidr(ip));
                    }
                    return;
                }
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
                    lock (result)
                    {
                        // skip TUN fake-ip answers (198.18.0.0/15), they are not the real server
                        foreach (var a in addrs.Where(a => !(a.AddressFamily == AddressFamily.InterNetwork
                            && a.GetAddressBytes() is [198, 18 or 19, ..])))
                        {
                            result.Add(ToCidr(a));
                        }
                    }
                }
                catch
                {
                    // unresolvable now; skip
                }
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ZapretManager", ex);
        }
        return result.OrderBy(s => s).ToList();

        static string ToCidr(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"{ip}/128" : $"{ip}/32";
    }

    private static bool WriteList(string name, IEnumerable<string> entries, string placeholder)
    {
        var lines = entries
            .Select(e => e.Trim())
            .Where(e => e.Length > 0 && !e.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (lines.Count == 0)
        {
            lines.Add(placeholder);
        }
        var content = "# Managed by the client, manual edits will be overwritten" + Environment.NewLine
            + string.Join(Environment.NewLine, lines) + Environment.NewLine;

        var path = Path.Combine(ListsDir, name);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }
        File.WriteAllText(path, content);
        return true;
    }

    #endregion user lists

    #region run

    /// <summary>Starts (or restarts) winws. Starts and stops are serialized: two at once kill each other.</summary>
    public async Task<(bool Success, string Message)> StartAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            return await StartCoreAsync();
        }
        finally
        {
            _runLock.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task<(bool Success, string Message)> StartCoreAsync()
    {
        if (UsesTpws)
        {
            return await StartTpwsAsync();
        }
        if (!IsInstalled)
        {
            return (false, "zapret is not installed");
        }
        if (!Utils.IsAdministrator())
        {
            return (false, "zapret needs administrator rights (WinDivert driver)");
        }

        var item = AppManager.Instance.Config.ZapretItem;
        var batPath = Path.Combine(ReleaseDir, item.Strategy);
        if (!File.Exists(batPath))
        {
            return (false, $"strategy not found: {item.Strategy}");
        }

        EnsureUserLists();
        var args = ZapretStrategyParser.Parse(await File.ReadAllTextAsync(batPath), BinDir, ListsDir, item.GameFilterMode, out var error);
        if (args is null)
        {
            return (false, $"{item.Strategy}: {error}");
        }

        // By default WinDivert catches outgoing packets on every interface, including VPN TUN adapters:
        // winws then mangles traffic inside the tunnel (fake packets, splits) and breaks every listed
        // site even when it goes through the VPN. Limit it to the physical network card.
        if (PhysicalInterfaceIndex() is { } ifIndex)
        {
            args.Insert(0, $"--wf-iface={ifIndex}");
        }

        await StopCoreAsync();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(BinDir, WinwsExe),
                WorkingDirectory = BinDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => LogOutput(e.Data);
            proc.Exited += (_, _) => StateChanged?.Invoke();
            proc.ErrorDataReceived += (_, e) => LogOutput(e.Data);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Kill winws together with the client, even on a crash.
            _job ??= new WindowsJobService();
            _job.AddProcess(proc.Handle);
            _process = proc;

            // winws exits right away on bad arguments or a WinDivert failure.
            await Task.Delay(1500);
            if (proc.HasExited)
            {
                return (false, $"winws exited with code {proc.ExitCode}, see log");
            }
            return (true, $"zapret started ({item.Strategy})");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Windows interface index of the default physical network card, or null if unknown.</summary>
    public static int? PhysicalInterfaceIndex()
    {
        try
        {
            var name = NetworkUtils.GetDefaultPhysicalInterfaceName();
            var ni = name.IsNullOrEmpty()
                ? null
                : System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
            return ni?.GetIPProperties().GetIPv4Properties()?.Index;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>Stops our winws and any other winws.exe instance (two instances conflict over WinDivert).</summary>
    public async Task StopAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _runLock.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task StopCoreAsync()
    {
        if (UsesTpws)
        {
            await StopTpwsAsync();
            return;
        }
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WinwsExe)))
        {
            try
            {
                p.Kill(true);
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
            finally
            {
                p.Dispose();
            }
        }
        _process?.Dispose();
        _process = null;
    }

    #endregion run

    #region auto-select strategy

    public record StrategyScore(string Strategy, int Passed, int Total, long ElapsedMs);

    /// <summary>
    /// URLs to check strategies against: domains of zapret and zapret→VPN rules, the latter even
    /// while on VPN fallback (the strategy is picked to get them off it). Probes go direct, so VPN
    /// rules would not tell anything. Falls back to Discord and YouTube.
    /// </summary>
    public static List<string> GetCheckUrls(Config config)
    {
        var urls = (config.ConnectionRules ?? [])
            .Where(r => r.Enabled && r.Mode is EConnectionMode.Zapret or EConnectionMode.ZapretThenVpn)
            .SelectMany(ZapretWatchdog.ProbeUrls)
            .Distinct()
            .ToList();
        return urls.Count > 0 ? urls : ["https://discord.com/", "https://www.youtube.com/"];
    }

    /// <summary>
    /// Tries strategies one by one and keeps the best: most URLs passing, then fastest.
    /// Restores the previous strategy if none passes anything.
    /// </summary>
    public async Task<(StrategyScore? Best, List<StrategyScore> All)> AutoSelectStrategyAsync(Config config, bool stopAtFirstFullPass = true,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        var item = config.ZapretItem;
        var original = item.Strategy;
        var urls = GetCheckUrls(config);

        var (best, all) = await SelectStrategyAsync(
            GetStrategies(),
            async strategy =>
            {
                item.Strategy = strategy;
                var (ok, msg) = await StartAsync();
                if (!ok)
                {
                    Logging.SaveLog($"{_tag}: {strategy}: {msg}");
                }
                return ok;
            },
            async ct =>
            {
                // Let winws hook in and old connections drop before measuring.
                await Task.Delay(1500, ct);
                var results = await Task.WhenAll(urls.Select(u => ZapretWatchdog.ProbeAsync(u, ct)));
                return results.Count(r => r);
            },
            urls.Count, stopAtFirstFullPass, progress, cancellationToken);

        item.Strategy = best is { Passed: > 0 } ? best.Strategy : original;
        await ConfigHandler.SaveConfig(config);
        if (item.Enabled)
        {
            await StartAsync();
        }
        else
        {
            await StopAsync();
        }
        return (best, all);
    }

    /// <summary>Selection loop, separated from process handling for testing.</summary>
    public static async Task<(StrategyScore? Best, List<StrategyScore> All)> SelectStrategyAsync(
        IReadOnlyList<string> strategies,
        Func<string, Task<bool>> apply,
        Func<CancellationToken, Task<int>> measure,
        int total,
        bool stopAtFirstFullPass,
        Func<string, Task>? progress,
        CancellationToken cancellationToken)
    {
        var scores = new List<StrategyScore>();
        StrategyScore? best = null;
        var index = 0;
        foreach (var strategy in strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            await Report(progress, $"[{index}/{strategies.Count}] {strategy}");

            if (!await apply(strategy))
            {
                scores.Add(new(strategy, 0, total, 0));
                continue;
            }

            var sw = Stopwatch.StartNew();
            var passed = await measure(cancellationToken);
            var score = new StrategyScore(strategy, passed, total, sw.ElapsedMilliseconds);
            scores.Add(score);
            await Report(progress, $"{strategy}: {passed}/{total}, {score.ElapsedMs} ms");

            if (best is null || passed > best.Passed || passed == best.Passed && score.ElapsedMs < best.ElapsedMs)
            {
                best = score;
            }
            if (stopAtFirstFullPass && passed == total)
            {
                break;
            }
        }
        return (best, scores);
    }

    #endregion auto-select strategy

    #region log

    private static void LogOutput(string? line)
    {
        if (line.IsNotEmpty())
        {
            Logging.SaveLog($"{_tag} winws: {line}");
        }
    }

    #endregion log

    private static async Task Report(Func<string, Task>? progress, string message)
    {
        if (progress != null)
        {
            await progress(message);
        }
    }
}
