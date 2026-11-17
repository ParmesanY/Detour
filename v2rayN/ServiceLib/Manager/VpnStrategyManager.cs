namespace ServiceLib.Manager;

/// <summary>
/// A way to cut the VPN connection's first packets so DPI can't read them, zapret-style: the TLS
/// handshake (ClientHello) or the first writes go out in small pieces with pauses between them.
/// Applied through Xray's fragment mask, so it works with any TLS/Reality server and on every OS.
/// </summary>
/// <param name="Winws">Windows: winws desync arguments applied to the traffic to the VPN server
/// (fake packets, splits — what Xray can't do); null for Xray fragment strategies.</param>
/// <param name="Transport">"mux" (Xray multiplexing) or "xmux" (XHTTP connection rotation): changes the
/// server's own settings instead of cutting packets; null for the other strategies.</param>
public sealed record VpnStrategy(string Name, string Description, string Packets, string Lengths, string Delays,
    string MaxSplit = "0", string? Winws = null, string? Transport = null)
{
    public bool UsesWinws => Winws.IsNotEmpty();
    public bool UsesTransport => Transport.IsNotEmpty();
}

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

    private static readonly IReadOnlyList<VpnStrategy> _builtin =
    [
        new("general", "ClientHello кусками по 100–200 байт, паузы 10–20 мс. Начните с неё.", "tlshello", "100-200", "10-20"),
        new("fine", "Мелкие куски 10–30 байт, паузы 5–10 мс: имя сайта (SNI) разрезано на части.", "tlshello", "10-30", "5-10"),
        new("tiny", "Совсем мелко, 3–8 байт: против DPI, который собирает куски обратно. Медленнее рукопожатие.", "tlshello", "3-8", "1-3"),
        new("slow", "Куски 30–60 байт с долгими паузами 50–100 мс: DPI не дожидается конца рукопожатия.", "tlshello", "30-60", "50-100"),
        new("first packets", "Режет первые три записи соединения, а не только рукопожатие: для протоколов без TLS.", "1-3", "40-80", "10-20"),
        new("split 2", "Только два куска: самый быстрый вариант, если провайдер мягкий.", "tlshello", "100-200", "5-10", "2"),
        // Windows: winws on the traffic to the VPN server. Needs zapret turned on (administrator rights).
        new("zapret fake", "zapret: перед рукопожатием уходит поддельный пакет, который сервер отбросит, а DPI примет за настоящий. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq"),
        new("zapret multisplit", "zapret: рукопожатие режется на части на уровне TCP, с перекрытием. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-split-seqovl=1"),
        new("zapret fakedsplit", "zapret: поддельные пакеты вперемешку с кусками рукопожатия, самый сильный вариант. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00"),
        // Settings of the server itself, saved for this server only.
        new("mux", "Мультиплексирование: много запросов идут внутри одного соединения, новых рукопожатий меньше. Не для XTLS-Vision. Только для этого сервера.",
            "", "", "", Transport: "mux"),
        new("xmux", "Для XHTTP: соединения к серверу сменяются раньше, чем провайдер успевает заморозить поток после ~16 КБ. Только для этого сервера.",
            "", "", "", Transport: "xmux"),
    ];

    /// <summary>XHTTP connection rotation: short-lived connections that stay under the 16 KB freeze.</summary>
    private const string XmuxJson = """{"maxConnections":"4-8","cMaxReuseTimes":"2-4","hMaxRequestTimes":"8-16","hMaxReusableSecs":"10-20"}""";

    /// <summary>Whether a mux / xmux strategy makes sense for this server (other strategies always do).</summary>
    public static bool Fits(VpnStrategy strategy, ProfileItem profile) => strategy.Transport switch
    {
        null or "" => true,
        "mux" => profile.ConfigType is EConfigType.VLESS or EConfigType.VMess or EConfigType.Trojan or EConfigType.Shadowsocks
                 && (profile.GetProtocolExtra().Flow ?? "").Length == 0
                 && profile.GetNetwork() != nameof(ETransport.xhttp),
        "xmux" => profile.GetNetwork() == nameof(ETransport.xhttp),
        _ => false,
    };

    /// <summary>A copy of the server with the strategy's own settings (mux on, or xmux in the XHTTP extra).</summary>
    public static ProfileItem WithTransport(ProfileItem profile, VpnStrategy? strategy)
    {
        if (strategy is not { UsesTransport: true } || !Fits(strategy, profile))
        {
            return profile;
        }
        var copy = JsonUtils.DeepCopy(profile);
        if (strategy.Transport == "mux")
        {
            copy.MuxEnabled = true;
            return copy;
        }
        var transport = copy.GetTransportExtra();
        var extra = (transport.XhttpExtra.IsNullOrEmpty() ? null : JsonNode.Parse(transport.XhttpExtra!) as JsonObject) ?? new JsonObject();
        extra["xmux"] = JsonNode.Parse(XmuxJson);
        copy.SetTransportExtra(transport with { XhttpExtra = extra.ToJsonString() });
        return copy;
    }

    /// <summary>Saves a mux / xmux strategy into the selected server (these are per-server settings).</summary>
    public static async Task ApplyToServerAsync(Config config, VpnStrategy strategy)
    {
        if (await CurrentServerAsync(config) is not { CustomJson: null } server)
        {
            return;
        }
        var updated = WithTransport(server.Profile, strategy);
        if (!ReferenceEquals(updated, server.Profile))
        {
            await SQLiteHelper.Instance.UpdateAsync(updated);
        }
    }

    private static volatile IReadOnlyList<VpnStrategy> _downloaded = [];

    /// <summary>Built-in strategies, updated and extended by the downloaded list; winws ones on Windows only.</summary>
    public static IReadOnlyList<VpnStrategy> Strategies
    {
        get
        {
            var byName = _downloaded.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            return _builtin.Select(s => byName.GetValueOrDefault(s.Name) ?? s)
                .Concat(_downloaded.Where(d => !_builtin.Any(s => s.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))))
                .Where(s => !s.UsesWinws || (Utils.IsWindows() && !ZapretManager.UsesTpws))
                .ToList();
        }
    }

    /// <summary>Published list, so strategies can change without a new release (like Flowseal's .bat updates).</summary>
    public const string ListPath = "strategies/vpn-strategies.json";
    private static string CachePath => Utils.GetConfigPath("vpnStrategies.json");
    private static readonly Regex _range = new(@"^\d{1,5}(-\d{1,5})?$");

    /// <summary>Validated strategies from the list JSON; anything odd is dropped.</summary>
    public static List<VpnStrategy> ParseList(string? json)
    {
        var list = JsonUtils.Deserialize<List<VpnStrategy>>(json) ?? [];
        return list.Where(s => s != null && s.Name.IsNotEmpty() && s.Name.Length <= 40 && s.Description.IsNotEmpty())
            .Select(s => s.UsesWinws ? s with { Winws = string.Join(' ', SafeWinwsArgs(s.Winws)) } : s)
            .Where(s => s.UsesTransport
                ? s.Transport is "mux" or "xmux"
                : s.UsesWinws
                ? s.Winws!.Length > 0
                : (s.Packets == "tlshello" || _range.IsMatch(s.Packets)) && _range.IsMatch(s.Lengths) && _range.IsMatch(s.Delays)
                  && _range.IsMatch(s.MaxSplit.IsNullOrEmpty() ? "0" : s.MaxSplit))
            .ToList();
    }

    public static void LoadCachedList()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                _downloaded = ParseList(File.ReadAllText(CachePath));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>Downloads the list from the project repository; keeps the old one on any failure.</summary>
    public static async Task UpdateListAsync(HttpClient client, CancellationToken ct)
    {
        if (AppUpdateManager.Repo is not { Length: > 0 } repo)
        {
            return;
        }
        try
        {
            var json = await client.GetStringAsync($"https://raw.githubusercontent.com/{repo}/main/{ListPath}", ct);
            var list = ParseList(json);
            if (list.Count > 0)
            {
                _downloaded = list;
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Logging.SaveLog($"{_tag}: list: {ex.Message}");
        }
    }

    /// <summary>The built-in list as JSON (what the published file starts from).</summary>
    public static string BuiltinJson() => JsonUtils.Serialize(_builtin.ToList());

    public static VpnStrategy? Find(string? name)
        => Strategies.FirstOrDefault(s => s.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the strategy into the settings (null = off). Xray strategies turn on the fragment mask;
    /// winws ones turn it off and take effect when zapret (re)starts.
    /// </summary>
    public static void Apply(Config config, VpnStrategy? strategy)
    {
        config.ZapretItem.VpnStrategy = strategy?.Name ?? string.Empty;
        config.CoreBasicItem.EnableFragment = strategy is { UsesWinws: false, UsesTransport: false };
        if (strategy is null or { UsesWinws: true } or { UsesTransport: true })
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
        var server = await CurrentServerAsync(config);
        if (server == null)
        {
            return new(VpnDiagnosis.NotTestable, null, [], AppText.VpnDiagNotTestable);
        }
        var (profile, host, port, customJson) = server.Value;

        progress?.Report(AppText.VpnDiagReach);
        var reachable = await TcpReachableAsync(host, port, ct);

        // winws strategies need zapret running: they restart it with a section for this server.
        var zapretRunning = ZapretManager.Instance.IsRunning;
        var results = new List<VpnStrategyResult>();
        try
        {
            var candidates = Strategies.Where(s => (!s.UsesWinws || zapretRunning)
                                                   && (!s.UsesTransport || (customJson == null && Fits(s, profile))));
            foreach (var strategy in new VpnStrategy?[] { null }.Concat(candidates))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(string.Format(AppText.VpnDiagTrying, strategy?.Name ?? AppText.VpnStrategyOff));
                if (strategy is { UsesWinws: true } || _winwsOverride != null)
                {
                    _winwsOverride = strategy is { UsesWinws: true } ? strategy : null;
                    await ZapretManager.Instance.StartAsync();
                }
                var xray = strategy is { UsesWinws: false } ? strategy : null;
                var result = customJson != null
                    ? await TestCustomAsync(customJson, xray, ct)
                    : await TestAsync(config, WithTransport(profile, strategy), xray, ct);
                results.Add(result with { Strategy = strategy });
                // Without a strategy it already works: nothing to pick.
                if (strategy == null && result.Works)
                {
                    break;
                }
            }
        }
        finally
        {
            if (_winwsOverride != null)
            {
                _winwsOverride = null;
                await ZapretManager.Instance.StartAsync();
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

    /// <summary>Strategy being tested by the picker; winws uses it instead of the saved one.</summary>
    private static volatile VpnStrategy? _winwsOverride;

    /// <summary>The selected server: its profile, address and port, and the custom Xray JSON if it is one.</summary>
    public static async Task<(ProfileItem Profile, string Host, int Port, string? CustomJson)?> CurrentServerAsync(Config config)
    {
        var profile = config.IndexId.IsNullOrEmpty() ? null : await AppManager.Instance.GetProfileItem(config.IndexId);
        if (profile == null)
        {
            return null;
        }
        if (profile.ConfigType == EConfigType.Custom)
        {
            var json = profile.CoreType is null or ECoreType.Xray ? ReadCustomConfig(profile.Address) : null;
            return json != null && CustomConfigFragment.IsXray(json) && CustomConfigFragment.FirstServer(json) is { } s
                ? (profile, s.Host, s.Port, json)
                : null;
        }
        return !profile.ConfigType.IsComplexType() && profile.Address.IsNotEmpty() && profile.Port > 0
            ? (profile, profile.Address, profile.Port, null)
            : null;
    }

    public const string VpnServerIpsetFile = "ipset-vpn-server.txt";

    /// <summary>
    /// winws arguments for the traffic to the selected VPN server (a profile placed before the strategy's own,
    /// ending with --new), or nothing when no winws strategy is chosen. Also returns the server port, which
    /// must be in winws' --wf-tcp filter.
    /// </summary>
    public static async Task<(List<string> Args, int Port)> WinwsSectionAsync(Config config, string listsDir)
    {
        var strategy = _winwsOverride ?? Find(config.ZapretItem.VpnStrategy);
        if (strategy is not { UsesWinws: true } || await CurrentServerAsync(config) is not { } server)
        {
            return ([], 0);
        }
        var ips = IPAddress.TryParse(server.Host.Trim('[', ']'), out var ip)
            ? [ip]
            : await ZapretWatchdog.ResolveDirectAsync(server.Host, ZapretWatchdog.PhysicalIPv4Address(), CancellationToken.None);
        if (ips.Length == 0)
        {
            return ([], 0);
        }
        var path = Path.Combine(listsDir, VpnServerIpsetFile);
        await File.WriteAllLinesAsync(path, ips.Select(a => a.ToString()));
        return ([$"--filter-tcp={server.Port}", $"--ipset={path}", .. SafeWinwsArgs(strategy.Winws), "--new"], server.Port);
    }

    /// <summary>
    /// Only --dpi-desync* options with plain values pass: the args go to winws running as administrator,
    /// and the strategy list can come from the network.
    /// </summary>
    public static List<string> SafeWinwsArgs(string? args)
        => (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(a => Regex.IsMatch(a, @"^--dpi-desync[a-z0-9-]*(=[A-Za-z0-9,.:+_-]*)?$"))
            .ToList();

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
