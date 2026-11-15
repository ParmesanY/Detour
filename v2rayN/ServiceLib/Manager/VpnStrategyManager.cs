namespace ServiceLib.Manager;

/// <summary>
/// A way to cut the VPN connection's first packets so DPI can't read them, zapret-style: the TLS
/// handshake (ClientHello) or the first writes go out in small pieces with pauses between them.
/// Applied through Xray's fragment mask, so it works with any TLS/Reality server and on every OS.
/// </summary>
public sealed record VpnStrategy(string Name, string Description, string Packets, string Lengths, string Delays, string MaxSplit = "0");

/// <summary>What happened with the server: reachability, and which strategies let traffic through.</summary>
public sealed record VpnStrategyResult(VpnStrategy? Strategy, bool Handshake, bool BigTransfer, int DelayMs)
{
    public bool Works => Handshake && BigTransfer;
}

public enum VpnDiagnosis
{
    /// <summary>Works without any strategy.</summary>
    Works,
    /// <summary>Works only with a strategy: DPI recognised the handshake.</summary>
    NeedsStrategy,
    /// <summary>The connection freezes after ~16 KB: a strategy helped (or not) with the first bytes.</summary>
    FreezesAfterHandshake,
    /// <summary>The server's address doesn't answer at all: IP block or the server is down.</summary>
    Unreachable,
    /// <summary>The address answers, but nothing gets through, whatever the strategy.</summary>
    Blocked,
    /// <summary>The server can't be tested (no server selected, custom JSON config…).</summary>
    NotTestable,
}

public sealed record VpnDiagnosisReport(VpnDiagnosis Diagnosis, VpnStrategy? Best, List<VpnStrategyResult> Results, string Text);

/// <summary>
/// "zapret for VPN": named strategies (like Flowseal's general / ALT .bat files) and an auto-pick that tries
/// each one on the selected server with a separate test core, then explains what is wrong with the server.
/// </summary>
public static class VpnStrategyManager
{
    private const string _tag = "VpnStrategy";

    /// <summary>A big download through the tunnel: the "16 KB" block freezes connections after 14–25 KB.</summary>
    public const string BigTransferUrl = "https://speed.cloudflare.com/__down?bytes=65536";
    private const int _bigTransferBytes = 64 * 1024;

    public static IReadOnlyList<VpnStrategy> Strategies { get; } =
    [
        new("general", "ClientHello кусками по 100–200 байт, паузы 10–20 мс. Начните с неё.", "tlshello", "100-200", "10-20"),
        new("fine", "Мелкие куски 10–30 байт, паузы 5–10 мс: имя сайта (SNI) разрезано на части.", "tlshello", "10-30", "5-10"),
        new("tiny", "Совсем мелко, 3–8 байт: против DPI, который собирает куски обратно. Медленнее рукопожатие.", "tlshello", "3-8", "1-3"),
        new("slow", "Куски 30–60 байт с долгими паузами 50–100 мс: DPI не дожидается конца рукопожатия.", "tlshello", "30-60", "50-100"),
        new("first packets", "Режет первые три записи соединения, а не только рукопожатие: для протоколов без TLS.", "1-3", "40-80", "10-20"),
        new("split 2", "Только два куска: самый быстрый вариант, если провайдер мягкий.", "tlshello", "100-200", "5-10", "2"),
    ];

    public static VpnStrategy? Find(string? name)
        => Strategies.FirstOrDefault(s => s.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the strategy into the core settings (null = fragmentation off).</summary>
    public static void Apply(Config config, VpnStrategy? strategy)
    {
        config.ZapretItem.VpnStrategy = strategy?.Name ?? string.Empty;
        config.CoreBasicItem.EnableFragment = strategy != null;
        if (strategy == null)
        {
            return;
        }
        config.Fragment4RayItem ??= new();
        config.Fragment4RayItem.Packets = strategy.Packets;
        config.Fragment4RayItem.Lengths = [strategy.Lengths];
        config.Fragment4RayItem.Delays = [strategy.Delays];
        config.Fragment4RayItem.MaxSplit = strategy.MaxSplit;
    }

    /// <summary>
    /// Tests the selected server: TCP reachability over the physical network card, then a separate test
    /// core per strategy (no strategy first) with a small request and a 64 KB download.
    /// </summary>
    public static async Task<VpnDiagnosisReport> DiagnoseAsync(Config config, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var profile = config.IndexId.IsNullOrEmpty() ? null : await AppManager.Instance.GetProfileItem(config.IndexId);
        // Custom (full JSON) Xray configs are tested as a copy with their own inbounds replaced.
        var customJson = profile?.ConfigType == EConfigType.Custom && profile.CoreType is null or ECoreType.Xray
            ? ReadCustomConfig(profile.Address)
            : null;
        var customServer = CustomConfigFragment.FirstServer(customJson);
        string host;
        int port;
        if (customJson != null && customServer is { } cs && CustomConfigFragment.IsXray(customJson))
        {
            (host, port) = cs;
        }
        else if (profile != null && !profile.ConfigType.IsComplexType() && profile.Address.IsNotEmpty() && profile.Port > 0)
        {
            (host, port, customJson) = (profile.Address, profile.Port, null);
        }
        else
        {
            return new(VpnDiagnosis.NotTestable, null, [], AppText.VpnDiagNotTestable);
        }

        progress?.Report(AppText.VpnDiagReach);
        var reachable = await TcpReachableAsync(host, port, ct);

        var results = new List<VpnStrategyResult>();
        foreach (var strategy in new VpnStrategy?[] { null }.Concat(Strategies))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(string.Format(AppText.VpnDiagTrying, strategy?.Name ?? AppText.VpnStrategyOff));
            var result = customJson != null ? await TestCustomAsync(customJson, strategy, ct) : await TestAsync(config, profile!, strategy, ct);
            results.Add(result);
            // Without a strategy it already works: nothing to pick.
            if (strategy == null && result.Works)
            {
                break;
            }
        }
        return Summarize(reachable, results);
    }

    /// <summary>Pure decision part, kept separate for tests.</summary>
    public static VpnDiagnosisReport Summarize(bool reachable, List<VpnStrategyResult> results)
    {
        var plain = results.FirstOrDefault(r => r.Strategy == null);
        var best = results.Where(r => r.Strategy != null && r.Works).OrderBy(r => r.DelayMs).FirstOrDefault();

        if (plain is { Works: true })
        {
            return new(VpnDiagnosis.Works, null, results, AppText.VpnDiagWorks);
        }
        if (best != null)
        {
            var kind = plain is { Handshake: true } ? VpnDiagnosis.FreezesAfterHandshake : VpnDiagnosis.NeedsStrategy;
            var text = kind == VpnDiagnosis.FreezesAfterHandshake ? AppText.VpnDiagFreezeFixed : AppText.VpnDiagNeedsStrategy;
            return new(kind, best.Strategy, results, string.Format(text, best.Strategy!.Name));
        }
        if (!reachable)
        {
            return new(VpnDiagnosis.Unreachable, null, results, AppText.VpnDiagUnreachable);
        }
        if (results.Any(r => r.Handshake))
        {
            return new(VpnDiagnosis.FreezesAfterHandshake, null, results, AppText.VpnDiagFreeze);
        }
        return new(VpnDiagnosis.Blocked, null, results, AppText.VpnDiagBlocked);
    }

    private static async Task<VpnStrategyResult> TestAsync(Config config, ProfileItem profile, VpnStrategy? strategy, CancellationToken ct)
    {
        var copy = JsonUtils.DeepCopy(config);
        Apply(copy, strategy);
        var item = new ServerTestItem
        {
            IndexId = profile.IndexId,
            Address = profile.Address,
            Port = profile.Port,
            ConfigType = profile.ConfigType,
            Profile = profile,
            CoreType = profile.CoreType ?? ECoreType.Xray,
            AllowTest = true,
        };
        ProcessService? process = null;
        try
        {
            process = await CoreManager.Instance.LoadCoreConfigSpeedtest(item, copy);
            if (process == null)
            {
                return new(strategy, false, false, -1);
            }
            await Task.Delay(1000, ct);
            using var handler = HttpClientHelper.CreateProxySocketsHttpHandler(item.Port, new());
            handler.ConnectTimeout = TimeSpan.FromSeconds(8);
            using var client = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan };
            var delay = await ConnectionHandler.GetRealPingTime(client, ct);
            var big = delay > 0 && await BigTransferAsync(client, ct);
            return new(strategy, delay > 0, big, delay);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(strategy, false, false, -1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.SaveLog($"{_tag}: {strategy?.Name ?? "off"}: {ex.Message}");
            return new(strategy, false, false, -1);
        }
        finally
        {
            if (process != null)
            {
                await process.StopAsync();
            }
        }
    }

    private static string? ReadCustomConfig(string? address)
    {
        if (address.IsNullOrEmpty())
        {
            return null;
        }
        var path = File.Exists(address) ? address : Utils.GetConfigPath(address);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static async Task<VpnStrategyResult> TestCustomAsync(string json, VpnStrategy? strategy, CancellationToken ct)
    {
        var port = Utils.GetFreePort(AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest));
        var testJson = CustomConfigFragment.BuildTestConfig(json, port, strategy);
        if (testJson == null)
        {
            return new(strategy, false, false, -1);
        }
        ProcessService? process = null;
        try
        {
            process = await CoreManager.Instance.RunTestConfigAsync(testJson, ECoreType.Xray);
            return process == null ? new(strategy, false, false, -1) : await MeasureAsync(port, strategy, ct);
        }
        finally
        {
            if (process != null)
            {
                await process.StopAsync();
            }
        }
    }

    /// <summary>Through the test core's local SOCKS port: a small request, then a 64 KB download.</summary>
    private static async Task<VpnStrategyResult> MeasureAsync(int port, VpnStrategy? strategy, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1000, ct);
            using var handler = HttpClientHelper.CreateProxySocketsHttpHandler(port, new());
            handler.ConnectTimeout = TimeSpan.FromSeconds(8);
            using var client = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan };
            var delay = await ConnectionHandler.GetRealPingTime(client, ct);
            var big = delay > 0 && await BigTransferAsync(client, ct);
            return new(strategy, delay > 0, big, delay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: {strategy?.Name ?? "off"}: {ex.Message}");
            return new(strategy, false, false, -1);
        }
    }

    private static async Task<bool> BigTransferAsync(HttpClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var response = await client.GetAsync(BigTransferUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[16384];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                total += read;
                if (total >= _bigTransferBytes)
                {
                    return true;
                }
            }
            return total >= 20 * 1024;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Plain TCP connect to the server over the physical network card (past any TUN).</summary>
    private static async Task<bool> TcpReachableAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var local = ZapretWatchdog.PhysicalIPv4Address();
            var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await ZapretWatchdog.ResolveDirectAsync(host, local, cts.Token);
            if (addresses.Length == 0)
            {
                return false;
            }
            await using var stream = await ZapretWatchdog.ConnectBoundAsync(addresses, port, local, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
