using System.IO.Compression;

namespace ServiceLib.Manager;

/// <summary>
/// zapret on macOS and Linux: tpws from the original bol-van/zapret release, run as a local SOCKS
/// proxy on 127.0.0.1. No root and no packet capture: the cores send "+zapret" traffic to it (see
/// <see cref="Global.ZapretOutboundRemarks" />) and tpws splits/disorders the TLS ClientHello on the
/// way out. TCP only; UDP (QUIC, Discord voice) is not processed.
/// </summary>
public partial class ZapretManager
{
    /// <summary>tpws instead of winws: every platform except Windows.</summary>
    public static bool UsesTpws => !Utils.IsWindows();

    private const string TpwsRepoUrl = "https://github.com/bol-van/zapret";
    public const int DefaultSocksPort = 10950;

    public static string TpwsDir => Path.Combine(RootDir, "tpws");
    public static string TpwsExePath => Path.Combine(TpwsDir, "tpws");

    /// <summary>Strategy name → tpws arguments. Picked by the same auto-select as on Windows.</summary>
    public static readonly IReadOnlyList<(string Name, string Args)> TpwsStrategies =
    [
        ("disorder (midsld)", "--split-pos=1,midsld --disorder"),
        ("tlsrec + disorder", "--tlsrec=sniext --split-pos=1,midsld --disorder"),
        ("oob (midsld)", "--split-pos=1,midsld --oob"),
        ("split (midsld)", "--split-pos=1,midsld"),
        ("tlsrec", "--tlsrec=sniext --split-pos=1"),
        ("disorder (sniext)", "--split-pos=sniext+1 --disorder"),
        ("hostcase + disorder", "--split-pos=1,midsld --disorder --hostcase --domcase"),
    ];

    public static string DefaultTpwsStrategy => TpwsStrategies[0].Name;

    /// <summary>tpws arguments of a strategy, or of the default one for an unknown name.</summary>
    public static List<string> TpwsArgs(string? strategy, int port)
    {
        var args = TpwsStrategies.FirstOrDefault(s => s.Name == strategy).Args ?? TpwsStrategies[0].Args;
        return ["--socks", "--bind-addr=127.0.0.1", $"--port={port}", .. args.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Folder of the release zip holding tpws for this machine.</summary>
    public static string? TpwsBinaryFolder()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (Utils.IsMacOS())
        {
            return "mac64"; // universal binary
        }
        if (Utils.IsLinux())
        {
            return arch switch
            {
                Architecture.X64 => "linux-x86_64",
                Architecture.Arm64 => "linux-arm64",
                Architecture.X86 => "linux-x86",
                Architecture.Arm => "linux-arm",
                _ => null,
            };
        }
        return null;
    }

    /// <summary>The SOCKS node "+zapret" rules are routed to while tpws runs.</summary>
    public static ProfileItem TpwsProxyNode(Config config) => new()
    {
        IndexId = "detour-zapret",
        ConfigType = EConfigType.SOCKS,
        Address = Global.Loopback,
        Port = config.ZapretItem.SocksPort,
        Remarks = Global.ZapretOutboundRemarks,
    };

    private async Task<(bool Success, string Message)> InstallTpwsAsync(DownloadService downloadService, string? version,
        Func<string, Task>? progress, CancellationToken cancellationToken)
    {
        var folder = TpwsBinaryFolder();
        if (folder is null)
        {
            return (false, $"tpws has no build for {RuntimeInformation.OSArchitecture}");
        }

        await _lock.WaitAsync(cancellationToken);
        var zipPath = Utils.GetTempPath($"zapret-{Utils.GetGuid()}.zip");
        try
        {
            if (version.IsNullOrEmpty() && AppUpdateManager.IsConfigured)
            {
                // Release builds install the version on Detour's signed list.
                version = TrustedAssets.TagFor(await TrustedAssets.LoadAsync(downloadService, cancellationToken), TpwsRepoUrl, ".zip");
            }
            else if (version.IsNullOrEmpty())
            {
                var location = await downloadService.ResolveFinalUrlAsync($"{TpwsRepoUrl}/releases/latest", cancellationToken);
                version = ParseReleaseTag(location);
            }
            if (version.IsNullOrEmpty())
            {
                return (false, "could not resolve the latest zapret release (GitHub unreachable?)");
            }

            var url = $"{TpwsRepoUrl}/releases/download/{version}/zapret-{version}.zip";
            await Report(progress, $"Downloading zapret {version}");
            string? downloadError = null;
            downloadService.Error += (_, e) => downloadError ??= e.GetException().Message;
            await downloadService.DownloadFileAsync(new() { FileUrl = url, FilePath = zipPath }, cancellationToken);
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                return (false, $"download failed: {downloadError ?? url}");
            }
            if (await TrustedAssets.CheckAsync(downloadService, TpwsRepoUrl, version!, $"zapret-{version}.zip", zipPath, cancellationToken) is { } digestError)
            {
                return (false, digestError);
            }

            await Report(progress, "Unpacking");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith($"binaries/{folder}/tpws", StringComparison.Ordinal));
                if (entry is null)
                {
                    return (false, $"binaries/{folder}/tpws not found in the archive");
                }
                var wasRunning = IsRunning;
                await StopAsync();
                Directory.CreateDirectory(TpwsDir);
                entry.ExtractToFile(TpwsExePath, true);
                File.SetUnixFileMode(TpwsExePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                if (wasRunning)
                {
                    await StartAsync();
                }
            }

            var config = AppManager.Instance.Config;
            config.ZapretItem.InstalledVersion = version;
            if (!GetStrategies().Contains(config.ZapretItem.Strategy))
            {
                config.ZapretItem.Strategy = DefaultTpwsStrategy;
            }
            await ConfigHandler.SaveConfig(config);
            return (true, $"zapret (tpws) {version} installed");
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
            }
            catch { }
            _lock.Release();
        }
    }

    private async Task<(bool Success, string Message)> StartTpwsAsync()
    {
        if (!File.Exists(TpwsExePath))
        {
            return (false, "zapret is not installed");
        }
        var item = AppManager.Instance.Config.ZapretItem;
        if (!GetStrategies().Contains(item.Strategy))
        {
            item.Strategy = DefaultTpwsStrategy;
        }

        await StopCoreAsync();
        if (TgWsProxyManager.IsPortBusy(item.SocksPort))
        {
            return (false, $"port {item.SocksPort} is used by another program");
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = TpwsExePath,
                WorkingDirectory = TpwsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in TpwsArgs(item.Strategy, item.SocksPort))
            {
                psi.ArgumentList.Add(a);
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => LogOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => LogOutput(e.Data);
            proc.Exited += (_, _) => StateChanged?.Invoke();
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _process = proc;

            // tpws exits right away on bad arguments or a busy port.
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                if (proc.HasExited)
                {
                    return (false, $"tpws exited with code {proc.ExitCode}, see log");
                }
                if (TgWsProxyManager.IsPortBusy(item.SocksPort))
                {
                    break;
                }
            }
            return (true, $"zapret started ({item.Strategy})");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Stops only our own tpws: a system-wide zapret install may run its own.</summary>
    private async Task StopTpwsAsync()
    {
        if (_process is { HasExited: false } p)
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
        }
        _process?.Dispose();
        _process = null;
    }
}
