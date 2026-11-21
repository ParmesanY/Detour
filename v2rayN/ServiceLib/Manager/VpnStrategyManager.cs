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
/// <param name="FrozenAtBytes">When the big download stalled: how many bytes came before it froze (the
/// "16–20 KB" block shows up as ~14–25 KB); 0 when it completed or never started.</param>
public sealed record VpnStrategyResult(VpnStrategy? Strategy, bool Handshake, bool BigTransfer, int DelayMs, int FrozenAtBytes = 0)
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
    /// <summary>The address answers ping but not TCP: the ISP blocked this IP (the server itself is up).</summary>
    IpBlocked,
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
        // TLS record split without TCP pauses: Xray's tlshello writes every piece as its own TLS record and, with
        // interval 0, sends them at once (freedom combines them into one TCP packet, #3663). Against a DPI that reassembles TCP (ТСПУ does) but parses
        // only the first record. CircumVolve (CCS 2026, table 5) and Niere et al. (S&P 2025): record splits with the
        // boundary inside the SNI / a first record under 4 bytes passed China, Pakistan, Kazakhstan; ~92% of servers
        // accept them. Not for REALITY: v2rayN turns tlshello into a TCP split there (#10428), see Fits.
        new("records", "Рукопожатие разрезано на TLS-записи по 10–30 байт и уходит без пауз: граница попадает внутрь имени сайта. Против DPI, который склеивает TCP, но читает только первую TLS-запись. Не для REALITY.",
            "tlshello", "10-30", "0"),
        new("records tiny first", "Первая TLS-запись 1–3 байта, остальное сразу следом: DPI не находит начало рукопожатия. Не для REALITY.",
            "tlshello", "1-3", "0", "2"),
        // Windows: winws on the traffic to the VPN server. Needs zapret turned on (administrator rights).
        new("zapret fake", "zapret: перед рукопожатием уходит поддельный пакет, который сервер отбросит, а DPI примет за настоящий. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq"),
        new("zapret multisplit", "zapret: рукопожатие режется на части на уровне TCP, с перекрытием. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-split-seqovl=1"),
        new("zapret fakedsplit", "zapret: поддельные пакеты вперемешку с кусками рукопожатия, самый сильный вариант. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00"),
        // A fake ClientHello with a whitelisted name: the 16–20 KB freeze spares whitelisted SNIs (net4people #490),
        // and a fork test (podkop-engine, 8 Oct 2026) passed 19/21 with spoofed whitelisted SNI + wrong seq/md5/timestamp,
        // 0/2 with a wrong checksum. Names from the cheburcheck whitelist (vk.com, yandex.ru, onetrust.com; max.ru isn't
        // there — and Flowseal's "max_ru" ClientHello file actually carries www.onetrust.com).
        new("zapret fake vk", "zapret: поддельный пакет с адресом vk.com (из белого списка) — DPI пропускает соединение как разрешённое. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com"),
        new("zapret fake vk md5", "zapret: то же, но поддельный пакет помечен TCP-MD5, а не сдвигом номера: другой способ, которым сервер его отбрасывает. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com"),
        new("zapret fake yandex", "zapret: поддельный пакет с адресом yandex.ru и разрезом рукопожатия, отбрасывается по устаревшей метке времени. Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1,sniext+1 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=yandex.ru"),
        new("zapret fake onetrust", "zapret: поддельный пакет с адресом www.onetrust.com (из белого списка; именно он стоял в замере 230 КБ/с против 48 Б/с). Нужен включённый zapret.",
            "", "", "", Winws: "--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-split-pos=1,sniext+1 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.onetrust.com"),
        // Settings of the server itself, saved for this server only.
        new("mux", "Мультиплексирование: много запросов идут внутри одного соединения, новых рукопожатий меньше. Не для XTLS-Vision. Только для этого сервера.",
            "", "", "", Transport: "mux"),
        // ТСПУ (06.2026) freezes a SNI after >3 near-simultaneous handshakes (habr.com/ru/articles/1047442), so
        // XHTTP keeps at most 2 connections; "xmux 6" (more handshakes) was dropped in 0.4.3.
        new("xmux 2", "Для XHTTP: не больше 2 соединений к серверу. Против поведенческой заморозки: когда к одному адресу открывают больше 3 соединений почти разом, провайдер замораживает их на 2 минуты. Только для этого сервера.",
            "", "", "", Transport: "xmux2"),
        new("xmux", "Для XHTTP: 1–2 соединения, которые сменяются раньше, чем провайдер успевает заморозить поток после ~16 КБ. Только для этого сервера.",
            "", "", "", Transport: "xmux"),
        new("xmux 3", "Для XHTTP: не больше 3 соединений к серверу. Только для этого сервера.",
            "", "", "", Transport: "xmux3"),
        // TLS fingerprint of the handshake: since 2026 ТСПУ targets Chrome 133 / Safari / iOS (Xray-core #7131), Firefox and Edge pass softer.
        new("отпечаток firefox", "Рукопожатие как у Firefox. Отпечаток Chrome (133) ТСПУ с 2026 года проверяет строже, Firefox проходит мягче. Только для этого сервера.",
            "", "", "", Transport: "fp:firefox"),
        new("отпечаток edge", "Рукопожатие как у Edge. Тоже проходит мягче Chrome. Только для этого сервера.", "", "", "", Transport: "fp:edge"),
        new("отпечаток chrome", "Рукопожатие как у Chrome. С 2026 года под подозрением у ТСПУ, пробуется после Firefox и Edge. Только для этого сервера.", "", "", "", Transport: "fp:chrome"),
        new("отпечаток safari", "Рукопожатие как у Safari. Только для этого сервера.", "", "", "", Transport: "fp:safari"),
        new("отпечаток qq", "Рукопожатие как у браузера QQ. Только для этого сервера.", "", "", "", Transport: "fp:qq"),
        new("отпечаток random", "Случайный браузерный отпечаток при каждом подключении. Только для этого сервера.", "", "", "", Transport: "fp:random"),
    ];

    /// <summary>XHTTP connection rotation: short-lived connections that stay under the 16 KB freeze.</summary>
    private const string XmuxJson = """{"maxConnections":"1-2","cMaxReuseTimes":"2-4","hMaxRequestTimes":"8-16","hMaxReusableSecs":"10-20"}""";

    private static readonly Regex _transport = new(@"^(mux|xmux[236]?|fp:(chrome|firefox|safari|ios|android|edge|360|qq|random|randomized))$");

    private static bool HasTls(ProfileItem profile)
        => profile.StreamSecurity == Global.StreamSecurity || profile.StreamSecurity == Global.StreamSecurityReality;

    /// <summary>Whether a per-server strategy makes sense for this server (other strategies always do).</summary>
    public static bool Fits(VpnStrategy strategy, ProfileItem profile) => strategy.Transport switch
    {
        null or "" => !IsRecordSplit(strategy) || profile.StreamSecurity == Global.StreamSecurity,
        "mux" => profile.ConfigType is EConfigType.VLESS or EConfigType.VMess or EConfigType.Trojan or EConfigType.Shadowsocks
                 && (profile.GetProtocolExtra().Flow ?? "").Length == 0
                 && profile.GetNetwork() != nameof(ETransport.xhttp),
        "xmux" or "xmux2" or "xmux3" or "xmux6" => profile.GetNetwork() == nameof(ETransport.xhttp),
        { } fp when fp.StartsWith("fp:", StringComparison.Ordinal) => HasTls(profile)
                                                                  && !fp[3..].Equals(profile.Fingerprint ?? "", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>TLS records in one packet (no pauses): only TLS servers, REALITY gets a plain TCP split instead.</summary>
    public static bool IsRecordSplit(VpnStrategy strategy)
        => strategy is { UsesWinws: false, UsesTransport: false, Packets: "tlshello", Delays: "0" or "0-0" };

    private static string XmuxFor(string transport) => transport switch
    {
        "xmux2" => """{"maxConnections":2}""",
        "xmux3" => """{"maxConnections":3}""",
        "xmux6" => """{"maxConnections":6}""",
        _ => XmuxJson,
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
        if (strategy.Transport!.StartsWith("fp:", StringComparison.Ordinal))
        {
            copy.Fingerprint = strategy.Transport[3..];
            return copy;
        }
        var transport = copy.GetTransportExtra();
        var extra = (transport.XhttpExtra.IsNullOrEmpty() ? null : JsonNode.Parse(transport.XhttpExtra!) as JsonObject) ?? new JsonObject();
        extra["xmux"] = JsonNode.Parse(XmuxFor(strategy.Transport));
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
                ? _transport.IsMatch(s.Transport!)
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
    /// Whether the selected server works again (a separate test core, the chosen disguise, a 64 KB download):
    /// the emergency mode's way back. False when it can't be tested.
    /// </summary>
    public static async Task<bool> CheckCurrentServerAsync(Config config, CancellationToken ct)
    {
        var server = await CurrentServerAsync(config);
        if (server == null)
        {
            return false;
        }
        var (profile, _, _, customJson) = server.Value;
        var xray = Find(config.ZapretItem.VpnStrategy) is { UsesWinws: false } s ? s : null;
        var result = customJson != null
            ? await TestCustomAsync(customJson, xray, ct)
            : await TestAsync(config, WithTransport(profile, xray), xray, ct);
        return result.Works;
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

        // Network level: a blocked IP still answers ping; a whole hosting network may be frozen.
        progress?.Report(AppText.VpnDiagNetwork);
        var ip = await FirstAddressAsync(host, ct);
        var pingOk = !reachable && ip != null && await ServerNetworkCheck.PingAsync(ip, ct);
        var network = ip != null ? await ServerNetworkCheck.LookupAsync(ip, ct) : null;
        if (network != null)
        {
            Logging.SaveLog($"{_tag}: server network AS{network.Asn} {network.AsName}{(network.Frozen != null ? " (frozen list)" : "")}");
        }

        // winws strategies need zapret running: they restart it with a section for this server.
        var zapretRunning = ZapretManager.Instance.IsRunning;
        var results = new List<VpnStrategyResult>();
        try
        {
            var candidates = Strategies.Where(s => (!s.UsesWinws || zapretRunning)
                                                   // custom JSON: no server settings to change or check
                                                   && (customJson != null ? !s.UsesTransport : Fits(s, profile)));
            // ТСПУ (06.2026) freezes a SNI for ~120 s after >3 handshakes to it within a minute, and longer when
            // the client keeps retrying (habr.com/ru/articles/1047442). Trying variants back to back is exactly
            // that: every variant after the first freeze would fail for nothing. So: a pause between variants,
            // a cool-down after a freeze, and the first variant that fully works ends the search.
            VpnStrategyResult? previous = null;
            foreach (var strategy in new VpnStrategy?[] { null }.Concat(candidates))
            {
                ct.ThrowIfCancellationRequested();
                if (previous != null)
                {
                    var pause = PauseAfter(previous);
                    progress?.Report(string.Format(AppText.VpnDiagPause, (int)pause.TotalSeconds));
                    await Task.Delay(pause, ct);
                }
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
                previous = result;
                // It works (without a strategy, or with this one): more handshakes would only risk a freeze.
                if (result.Works)
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
        return Summarize(reachable, results, pingOk, network);
    }

    private static async Task<IPAddress?> FirstAddressAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var ip)
                ? [ip]
                : await ZapretWatchdog.ResolveDirectAsync(host, ZapretWatchdog.PhysicalIPv4Address(), ct);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is SocketException or IOException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Pure decision part, kept separate for tests. A frozen hosting network adds its note to any failure.</summary>
    /// <summary>
    /// How long to wait before the next variant: 15 s keeps it at ≤ 3-4 handshakes a minute to one server; after
    /// a freeze (the handshake passed, the data stopped) or a failed handshake right after one, 125 s for the
    /// freeze to run out, or the next variants would be measured inside it.
    /// </summary>
    public static TimeSpan PauseAfter(VpnStrategyResult previous)
        => previous.FrozenAtBytes > 0 || (previous.Handshake && !previous.BigTransfer)
            ? TimeSpan.FromSeconds(125)
            : TimeSpan.FromSeconds(15);

    public static VpnDiagnosisReport Summarize(bool reachable, List<VpnStrategyResult> results, bool pingOk = false,
        ServerNetwork? network = null)
    {
        var report = SummarizeCore(reachable, results, pingOk);
        return network?.Frozen is { } frozen && report.Diagnosis != VpnDiagnosis.Works
            ? report with { Text = report.Text + "\n" + string.Format(AppText.VpnDiagFrozenNetwork, frozen.Name, network.Asn) }
            : report;
    }

    private static VpnDiagnosisReport SummarizeCore(bool reachable, List<VpnStrategyResult> results, bool pingOk)
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
            return pingOk
                ? new(VpnDiagnosis.IpBlocked, null, results, AppText.VpnDiagIpBlocked)
                : new(VpnDiagnosis.Unreachable, null, results, AppText.VpnDiagUnreachable);
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
            var (big, bytes) = delay > 0 ? await BigTransferAsync(client, ct) : (false, 0);
            return new(strategy, delay > 0, big, delay, big ? 0 : bytes);
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
            // "=" inside the value is needed for --dpi-desync-fake-tls-mod=rnd,sni=vk.com; args go through
            // ProcessStartInfo.ArgumentList (no shell), so it carries no meaning beyond winws' own parser.
            .Where(a => Regex.IsMatch(a, @"^--dpi-desync[a-z0-9-]*(=[A-Za-z0-9,.:+_=-]*)?$"))
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
            var (big, bytes) = delay > 0 ? await BigTransferAsync(client, ct) : (false, 0);
            return new(strategy, delay > 0, big, delay, big ? 0 : bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: {strategy?.Name ?? "off"}: {ex.Message}");
            return new(strategy, false, false, -1);
        }
    }

    /// <summary>A 64 KB download: whether it completed, and how many bytes arrived (where it froze if not).</summary>
    private static async Task<(bool Ok, int Bytes)> BigTransferAsync(HttpClient client, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        var total = 0;
        try
        {
            using var response = await client.GetAsync(BigTransferUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                total += read;
                if (total >= _bigTransferBytes)
                {
                    return (true, total);
                }
            }
            return (total >= 20 * 1024, total);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (false, total);
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
