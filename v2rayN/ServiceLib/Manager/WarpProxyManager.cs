using System.Net;

namespace ServiceLib.Manager;

/// <summary>
/// The WARP page's local proxy: a separate Xray process (the bundled core, nothing to download) that
/// listens on 127.0.0.1:Port as SOCKS and sends everything through Cloudflare WARP. Connection rules
/// with "+warp" are routed to it by the main core. It is not the official Cloudflare WARP client: that
/// one brings its own TUN adapter, which fights with Detour's.
/// Under TUN the main core already sends bin\xray\xray.exe direct by process, so WARP's own UDP to
/// Cloudflare never loops back into the tunnel.
/// </summary>
public sealed class WarpProxyManager
{
    private static readonly Lazy<WarpProxyManager> _instance = new(() => new());
    public static WarpProxyManager Instance => _instance.Value;
    private const string _tag = "WarpProxy";

    public const int DefaultPort = 40000;
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private WindowsJobService? _job;
    private StreamWriter? _log;

    /// <summary>Raised after the proxy was started, stopped or exited on its own (any thread).</summary>
    public event Action? StateChanged;

    public static string RootDir => Utils.GetBinPath("", "warp");
    public static string ConfigPath => Path.Combine(RootDir, "config.json");
    public static string LogPath => Path.Combine(RootDir, "warp.log");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The SOCKS node "+warp" rules are routed to (not stored in the server list).</summary>
    public static ProfileItem LocalProxyNode(Config config) => new()
    {
        IndexId = "detour-warp",
        ConfigType = EConfigType.SOCKS,
        Address = Global.Loopback,
        Port = config.WarpItem.Port,
        Remarks = Global.WarpOutboundRemarks,
    };

    public async Task<(bool Success, string Message)> StartAsync()
    {
        var config = AppManager.Instance.Config;
        var item = config.WarpItem;
        var reg = WarpManager.FromItem(item);
        if (reg is null)
        {
            return (false, AppText.WarpNotRegistered);
        }
        var exe = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray), out _);
        if (exe.IsNullOrEmpty() || !File.Exists(exe))
        {
            return (false, AppText.WarpNoXray);
        }

        await StopAsync();
        await _lock.WaitAsync();
        try
        {
            if (TgWsProxyManager.IsPortBusy(item.Port))
            {
                return (false, string.Format(AppText.WarpPortBusy, item.Port));
            }
            Directory.CreateDirectory(RootDir);
            await File.WriteAllTextAsync(ConfigPath, WarpManager.BuildXrayConfig(reg, item.Port, item.Endpoint, item.Noise));

            _log = new StreamWriter(LogPath, false) { AutoFlush = true };
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"run -c \"{ConfigPath}\"",
                WorkingDirectory = RootDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => WriteLog(e.Data);
            proc.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            proc.Exited += (_, _) => StateChanged?.Invoke();
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _job ??= new WindowsJobService();
            _job.AddProcess(proc.Handle);
            _process = proc;

            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(250);
                if (proc.HasExited)
                {
                    return (false, string.Format(AppText.WarpExited, proc.ExitCode));
                }
                if (TgWsProxyManager.IsPortBusy(item.Port))
                {
                    Logging.SaveLog($"{_tag}: started on 127.0.0.1:{item.Port}, endpoint {item.Endpoint}, noise={item.Noise}");
                    return (true, AppText.WarpRunning);
                }
            }
            return (true, AppText.WarpRunning);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.Message);
        }
        finally
        {
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_process is { HasExited: false } p)
            {
                p.Kill(true);
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _log?.Dispose();
            _log = null;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }
        try
        {
            _log?.WriteLine(line);
        }
        catch
        {
            // log closed
        }
    }

    public record TraceResult(bool Ok, bool WarpOn, string? Colo, string? Location, long Ms, string? Error);

    /// <summary>Asks Cloudflare through the local proxy whether the request came over WARP.</summary>
    public async Task<TraceResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var port = AppManager.Instance.Config.WarpItem.Port;
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var text = await client.GetStringAsync(TraceUrl, cancellationToken);
            var trace = ParseTrace(text);
            return new TraceResult(true, trace.GetValueOrDefault("warp") is "on" or "plus",
                trace.GetValueOrDefault("colo"), trace.GetValueOrDefault("loc"), sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new TraceResult(false, false, null, null, sw.ElapsedMilliseconds, ex.GetBaseException().Message);
        }
    }

    /// <summary>"key=value" lines of /cdn-cgi/trace.</summary>
    public static Dictionary<string, string> ParseTrace(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }
        return result;
    }
}
