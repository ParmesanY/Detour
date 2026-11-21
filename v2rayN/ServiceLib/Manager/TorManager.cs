using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ServiceLib.Manager;

/// <summary>
/// Tor for the emergency mode: a lifeline when every VPN server is blocked. tor.exe from the Tor Expert Bundle
/// (shipped in the release archive: torproject.org is blocked in Russia) with bridges through lyrebird:
/// Snowflake by default (volunteers' WebRTC proxies, the lines Tor Project recommends for Russia), or the
/// user's own WebTunnel / obfs4 bridges from @GetBridgesBot. Detour is only a Tor client: never a relay.
/// Tor listens on a local SOCKS port; the emergency mode routes what normally goes through the VPN there.
/// </summary>
public sealed class TorManager
{
    private static readonly Lazy<TorManager> _instance = new(() => new());
    public static TorManager Instance => _instance.Value;
    private const string _tag = "Tor";

    public const string ExeName = "tor.exe";
    public const string TransportExeName = "lyrebird.exe";
    public static readonly TimeSpan BootstrapTimeout = TimeSpan.FromMinutes(4);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private WindowsJobService? _job;
    private StreamWriter? _log;
    private TaskCompletionSource<bool>? _ready;

    /// <summary>Started, stopped, exited or progressed (any thread).</summary>
    public event Action? StateChanged;

    public static string BinDir => Utils.GetBinPath("", "tor");
    public static string ExePath => Path.Combine(BinDir, ExeName);
    public static string TransportPath => Path.Combine(BinDir, TransportExeName);
    public static string DataDir => Utils.GetConfigPath("tor");
    public static string TorrcPath => Path.Combine(DataDir, "torrc");
    public static string LogPath => Path.Combine(DataDir, "tor.log");

    public static bool IsInstalled => File.Exists(ExePath) && File.Exists(TransportPath);
    public bool IsRunning => _process is { HasExited: false };
    /// <summary>Bootstrap progress 0..100; 100 = connected to the Tor network.</summary>
    public int Progress { get; private set; }
    public bool IsReady => IsRunning && Progress >= 100;
    public int Port { get; private set; }

    /// <summary>The bridges in use: "snowflake" or "user".</summary>
    public string BridgeKind { get; private set; } = "";

    #region pure helpers

    /// <summary>
    /// Snowflake bridge lines Tor Project recommends for Russia (circumvention map, 11.2026): CDN77 fronting and
    /// the AMP-cache one through www.google.com for when CDN77 is blocked (as in 10.2025).
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltinSnowflake =
    [
        "snowflake 192.0.2.5:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 fingerprint=2B280B23E1107BB62ABFC40DDCC8824814F80A72 url=https://snowflake-broker.torproject.net/ ampcache=https://cdn.ampproject.org/ front=www.google.com ice=stun:stun.antisip.com:3478,stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.telnyx.com:3478,stun:stun.hot-chilli.net:3478,stun:stun.fitauto.ru:3478,stun:stun.m-online.net:3478 utls-imitate=hellorandomizedalpn",
        "snowflake 192.0.2.6:80 8838024498816A039FCBBAB14E6F40A0843051FA fingerprint=8838024498816A039FCBBAB14E6F40A0843051FA url=https://snowflake-broker.torproject.net/ ampcache=https://cdn.ampproject.org/ front=www.google.com ice=stun:stun.antisip.com:3478,stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.telnyx.com:3478,stun:stun.hot-chilli.net:3478,stun:stun.fitauto.ru:3478,stun:stun.m-online.net:3478 utls-imitate=hellorandomizedalpn",
        "snowflake 192.0.2.3:80 2B280B23E1107BB62ABFC40DDCC8824814F80A72 fingerprint=2B280B23E1107BB62ABFC40DDCC8824814F80A72 url=https://1098762253.rsc.cdn77.org fronts=cdn.zk.mk,img.icons8.com,cdn.kde.org ice=stun:stun.antisip.com:3478,stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.telnyx.com:3478,stun:stun.hot-chilli.net:3478,stun:stun.fitauto.ru:3478,stun:stun.m-online.net:3478 utls-imitate=hellorandomizedalpn",
        "snowflake 192.0.2.4:80 8838024498816A039FCBBAB14E6F40A0843051FA fingerprint=8838024498816A039FCBBAB14E6F40A0843051FA url=https://1098762253.rsc.cdn77.org fronts=cdn.zk.mk,img.icons8.com,cdn.kde.org ice=stun:stun.antisip.com:3478,stun:stun.epygi.com:3478,stun:stun.uls.co.za:3478,stun:stun.telnyx.com:3478,stun:stun.hot-chilli.net:3478,stun:stun.fitauto.ru:3478,stun:stun.m-online.net:3478 utls-imitate=hellorandomizedalpn",
    ];

    private static readonly Regex _bridgeLine = new(@"^(webtunnel|obfs4|snowflake)\s+\S+(\s+[A-Fa-f0-9]{40})?(\s+\S+=\S*)*$", RegexOptions.Compiled);

    /// <summary>
    /// Bridge lines the user pasted (from @GetBridgesBot or bridges.torproject.org): "Bridge " prefixes and
    /// blank lines dropped, only webtunnel / obfs4 / snowflake lines kept, nothing that could inject torrc options.
    /// </summary>
    public static List<string> ParseBridges(string? text)
        => (text ?? "").Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Select(l => l.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase) ? l[7..].Trim() : l)
            .Where(l => l.Length is > 0 and < 2000 && !l.Contains('\n') && _bridgeLine.IsMatch(l))
            .Distinct()
            .ToList();

    /// <summary>The torrc: SOCKS on loopback only, the bridges, lyrebird for their transports.</summary>
    public static string BuildTorrc(int port, IReadOnlyList<string> bridges, string transportPath, string dataDir)
    {
        static string Q(string path) => "\"" + path.Replace("\\", "\\\\") + "\"";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Detour emergency mode. Client only: no relay, no exit, no directory.");
        sb.AppendLine($"SocksPort 127.0.0.1:{port}");
        sb.AppendLine("SocksPolicy accept 127.0.0.1, reject *");
        sb.AppendLine("ClientOnly 1");
        sb.AppendLine("AvoidDiskWrites 1");
        sb.AppendLine($"DataDirectory {Q(dataDir)}");
        sb.AppendLine("Log notice stdout");
        sb.AppendLine("UseBridges 1");
        // Relative to tor's working directory (bin\tor), as Tor Browser does: tor keeps a quoted path's quotes
        // and CreateProcess then fails, and an unquoted one breaks on spaces in the Detour folder.
        sb.AppendLine($"ClientTransportPlugin snowflake,webtunnel,obfs4 exec {Path.GetFileName(transportPath)}");
        foreach (var b in bridges)
        {
            sb.AppendLine($"Bridge {b}");
        }
        return sb.ToString();
    }

    private static readonly Regex _bootstrapped = new(@"Bootstrapped (\d{1,3})%", RegexOptions.Compiled);

    /// <summary>Bootstrap percent from a log line, or null.</summary>
    public static int? ParseProgress(string? line)
        => line != null && _bootstrapped.Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var p) ? p : null;

    /// <summary>tor.exe and lyrebird.exe go direct: they reach bridges themselves (also past the TUN).</summary>
    public static RulesItem DirectRule() => new()
    {
        Id = Utils.GetGuid(false),
        Remarks = ConnectionRuleCompiler.RemarksPrefix + "Tor",
        OutboundTag = Global.DirectTag,
        Process = [ExeName, TransportExeName],
        Enabled = true,
        RuleType = ERuleType.Routing,
    };

    /// <summary>The emergency mode's "server": Tor's local SOCKS port.</summary>
    public ProfileItem LocalProxyNode() => new()
    {
        IndexId = "detour-tor",
        ConfigType = EConfigType.SOCKS,
        Address = Global.Loopback,
        Port = Port,
        Remarks = "Tor",
    };

    #endregion pure helpers

    /// <summary>
    /// Starts tor and waits until it's connected to the Tor network (bootstrap 100%), up to 4 minutes.
    /// <paramref name="progress"/> gets the percent as it goes.
    /// </summary>
    public async Task<(bool Success, string Message)> StartAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!IsInstalled)
        {
            return (false, AppText.TorNotInstalled);
        }
        await StopAsync();
        await _lock.WaitAsync(ct);
        TaskCompletionSource<bool> ready;
        try
        {
            var config = AppManager.Instance.Config;
            var user = ParseBridges(config.EmergencyItem.Bridges);
            var bridges = user.Count > 0 ? user : [.. BuiltinSnowflake];
            BridgeKind = user.Count > 0 ? "user" : "snowflake";
            Port = Utils.GetFreePort(AppManager.Instance.GetLocalPort(EInboundProtocol.socks) + 60);
            Directory.CreateDirectory(DataDir);
            await File.WriteAllTextAsync(TorrcPath, BuildTorrc(Port, bridges, TransportPath, Path.Combine(DataDir, "data")), ct);

            Progress = 0;
            ready = _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _log = new StreamWriter(LogPath, false) { AutoFlush = true };
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"-f \"{TorrcPath}\"",
                    WorkingDirectory = BinDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            proc.OutputDataReceived += (_, e) => OnLine(e.Data, progress);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data, progress);
            proc.Exited += (_, _) =>
            {
                _ready?.TrySetResult(false);
                StateChanged?.Invoke();
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (Utils.IsWindows())
            {
                _job ??= new WindowsJobService();
                _job.AddProcess(proc.Handle);
            }
            _process = proc;
            Logging.SaveLog($"{_tag}: started on 127.0.0.1:{Port} with {bridges.Count} {BridgeKind} bridges");
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

        var finished = await Task.WhenAny(ready.Task, Task.Delay(BootstrapTimeout, ct));
        if (finished == ready.Task && ready.Task.Result)
        {
            return (true, AppText.TorConnected);
        }
        var stuckAt = Progress;
        await StopAsync();
        return (false, string.Format(AppText.TorFailed, stuckAt));
    }

    private void OnLine(string? line, IProgress<int>? progress)
    {
        if (line == null)
        {
            return;
        }
        try
        {
            _log?.WriteLine(line);
        }
        catch
        {
        }
        if (ParseProgress(line) is { } p && p > Progress)
        {
            Progress = p;
            progress?.Report(p);
            StateChanged?.Invoke();
            if (p >= 100)
            {
                _ready?.TrySetResult(true);
            }
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
            Progress = 0;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }
}
