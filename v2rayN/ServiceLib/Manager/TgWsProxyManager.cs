using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ServiceLib.Manager;

/// <summary>
/// Installs, updates and runs TG WS Proxy (Flowseal/tg-ws-proxy, MIT), Windows only.
/// The release is a single tray exe. It is kept in bin/tgwsproxy and run in portable mode
/// (settings in bin/tgwsproxy/TgWsProxy_data/config.json), so a separately installed copy of
/// the user's is left alone. The client owns host, port and secret in that config.
/// </summary>
public class TgWsProxyManager
{
    private static readonly Lazy<TgWsProxyManager> _instance = new(() => new());
    public static TgWsProxyManager Instance => _instance.Value;

    private const string _tag = "TgWsProxyManager";
    public const string RepoUrl = "https://github.com/Flowseal/tg-ws-proxy";
    public const string ExeName = "TgWsProxy.exe";
    public const string Host = "127.0.0.1";
    public const int DefaultPort = 1443;
    private const string DataDirName = "TgWsProxy_data";
    private const string FirstRunMarker = ".first_run_done_mtproto";
    private const long MinExeSize = 1_000_000;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private WindowsJobService? _job;

    /// <summary>Raised after the proxy was started, stopped or exited on its own (any thread).</summary>
    public event Action? StateChanged;

    public static string RootDir => Utils.GetBinPath("", "tgwsproxy");
    public static string ExePath => Path.Combine(RootDir, ExeName);
    public static string DataDir => Path.Combine(RootDir, DataDirName);
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string LogPath => Path.Combine(DataDir, "proxy.log");

    public bool IsInstalled => File.Exists(ExePath);

    public bool IsRunning => _process is { HasExited: false };

    #region pure helpers

    /// <summary>Release asset for this machine: the x64 build, or the arm64 one on ARM.</summary>
    public static string AssetName(Architecture arch)
    {
        return arch == Architecture.Arm64 ? "TgWsProxy_windows_arm64.exe" : "TgWsProxy_windows.exe";
    }

    /// <summary>A valid MTProto secret: exactly 32 hex characters.</summary>
    public static bool IsValidSecret(string? secret)
    {
        return secret is { Length: 32 } && secret.All(Uri.IsHexDigit);
    }

    public static string NewSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>Link that adds the proxy to Telegram. "dd" = padded intermediate, same as the tray app.</summary>
    public static string BuildLink(int port, string secret)
    {
        return $"tg://proxy?server={Host}&port={port}&secret=dd{secret}";
    }

    /// <summary>
    /// The proxy's config.json with our host/port/secret, keeping its other settings.
    /// Its own update checks and Windows autostart are turned off: the client handles both.
    /// </summary>
    public static string BuildConfigJson(string? existingJson, int port, string secret)
    {
        JsonObject cfg;
        try
        {
            cfg = existingJson.IsNullOrEmpty() ? [] : JsonNode.Parse(existingJson!) as JsonObject ?? [];
        }
        catch
        {
            cfg = [];
        }
        cfg["host"] = Host;
        cfg["port"] = port;
        cfg["secret"] = secret;
        cfg["check_updates"] = false;
        cfg["autostart"] = false;
        return cfg.ToJsonString(new() { WriteIndented = true });
    }

    #endregion pure helpers

    #region install / update

    public async Task<string?> GetLatestVersionAsync(DownloadService downloadService, CancellationToken cancellationToken = default)
    {
        var location = await downloadService.ResolveFinalUrlAsync($"{RepoUrl}/releases/latest", cancellationToken);
        return ZapretManager.ParseReleaseTag(location);
    }

    public async Task<(bool Success, string Message)> InstallAsync(DownloadService downloadService, string? version = null,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Utils.IsWindows())
        {
            return (false, "TG WS Proxy is supported on Windows only");
        }

        await _lock.WaitAsync(cancellationToken);
        var tmpPath = Utils.GetTempPath($"tgwsproxy-{Utils.GetGuid()}.exe");
        try
        {
            version ??= await GetLatestVersionAsync(downloadService, cancellationToken);
            if (version.IsNullOrEmpty())
            {
                return (false, "could not resolve the latest TG WS Proxy release (GitHub unreachable?)");
            }

            var url = $"{RepoUrl}/releases/download/{version}/{AssetName(RuntimeInformation.OSArchitecture)}";
            await Report(progress, $"Downloading TG WS Proxy {version}");
            string? downloadError = null;
            downloadService.Error += (_, e) => downloadError ??= e.GetException().Message;
            await downloadService.DownloadFileAsync(new() { FileUrl = url, FilePath = tmpPath }, cancellationToken);
            if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length < MinExeSize)
            {
                return (false, $"download failed: {downloadError ?? url}");
            }

            var wasRunning = IsRunning;
            await StopAsync();

            Directory.CreateDirectory(RootDir);
            File.Copy(tmpPath, ExePath, true);

            var config = AppManager.Instance.Config;
            config.TgWsProxyItem.InstalledVersion = version;
            await ConfigHandler.SaveConfig(config);

            if (wasRunning)
            {
                await StartAsync();
            }
            return (true, $"TG WS Proxy {version} installed");
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
                File.Delete(tmpPath);
            }
            catch { }
            _lock.Release();
        }
    }

    #endregion install / update

    #region run

    /// <summary>The secret from the settings, generated and saved on first use.</summary>
    public static async Task<string> EnsureSecretAsync(Config config)
    {
        var item = config.TgWsProxyItem;
        if (!IsValidSecret(item.Secret))
        {
            item.Secret = NewSecret();
            await ConfigHandler.SaveConfig(config);
        }
        return item.Secret!;
    }

    public static string? GetLink(Config config)
    {
        var item = config.TgWsProxyItem;
        return IsValidSecret(item.Secret) ? BuildLink(item.Port, item.Secret!) : null;
    }

    public async Task<(bool Success, string Message)> StartAsync()
    {
        if (!Utils.IsWindows())
        {
            return (false, "TG WS Proxy is supported on Windows only");
        }
        if (!IsInstalled)
        {
            return (false, AppText.TgNotInstalled);
        }

        var config = AppManager.Instance.Config;
        var item = config.TgWsProxyItem;
        var secret = await EnsureSecretAsync(config);

        await StopAsync();

        // The tray app allows one instance per Windows session (named mutex) and shows a blocking
        // "already running" box otherwise: a copy the user runs on their own must be closed first.
        if (FindForeignInstance() is { } foreign)
        {
            return (false, string.Format(AppText.TgForeignRunning, foreign));
        }
        if (IsPortBusy(item.Port))
        {
            return (false, string.Format(AppText.TgPortBusy, item.Port));
        }

        try
        {
            Directory.CreateDirectory(DataDir);
            var existing = File.Exists(ConfigPath) ? await File.ReadAllTextAsync(ConfigPath) : null;
            await File.WriteAllTextAsync(ConfigPath, BuildConfigJson(existing, item.Port, secret));
            // Skip the first-run instructions window; the client explains the setup itself.
            var marker = Path.Combine(DataDir, FirstRunMarker);
            if (!File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker, "");
            }

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                WorkingDirectory = RootDir,
                UseShellExecute = false,
                Arguments = "--portable",
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) => StateChanged?.Invoke();
            proc.Start();

            // Close it together with the client, even on a crash.
            _job ??= new WindowsJobService();
            _job.AddProcess(proc.Handle);
            _process = proc;

            // The onefile build unpacks itself first; give it time to bind the port.
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                if (proc.HasExited)
                {
                    return (false, string.Format(AppText.TgExited, proc.ExitCode, LogPath));
                }
                if (IsPortBusy(item.Port))
                {
                    return (true, AppText.TgRunning);
                }
            }
            return (true, AppText.TgStartedNoPort);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.Message);
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    /// <summary>Stops our copy (the whole process tree: the onefile build runs as two processes).</summary>
    public async Task StopAsync()
    {
        foreach (var p in OwnProcesses())
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
        StateChanged?.Invoke();
    }

    private static IEnumerable<Process> OwnProcesses()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            if (ProcessPath(p) is { } path && path.Equals(ExePath, StringComparison.OrdinalIgnoreCase))
            {
                yield return p;
            }
            else
            {
                p.Dispose();
            }
        }
    }

    /// <summary>File name of a TG WS Proxy run from somewhere else, or null.</summary>
    private static string? FindForeignInstance()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!p.ProcessName.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var path = ProcessPath(p);
                if (path is null || !path.Equals(ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    return path ?? p.ProcessName + ".exe";
                }
            }
        }
        return null;
    }

    private static string? ProcessPath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Something listens on 127.0.0.1:port.</summary>
    public static bool IsPortBusy(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens the tg://proxy link: Telegram Desktop asks to add and enable the proxy.</summary>
    public static bool OpenInTelegram(Config config)
    {
        var link = GetLink(config);
        if (link is null)
        {
            return false;
        }
        ProcUtils.ProcessStart(link);
        return true;
    }

    #endregion run

    #region routing

    /// <summary>
    /// Core rule that keeps the proxy's own traffic off the VPN: it reaches Telegram directly over
    /// WebSocket, that is the whole point. Placed before the user's rules.
    /// </summary>
    public static RulesItem DirectRule()
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            Remarks = ConnectionRuleCompiler.RemarksPrefix + "TG WS Proxy",
            OutboundTag = Global.DirectTag,
            Process = [ExeName],
            Enabled = true,
            RuleType = ERuleType.Routing,
        };
    }

    #endregion routing

    private static async Task Report(Func<string, Task>? progress, string message)
    {
        if (progress != null)
        {
            await progress(message);
        }
    }
}
