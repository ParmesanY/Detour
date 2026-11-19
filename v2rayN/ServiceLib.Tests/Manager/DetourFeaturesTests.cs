using System.Text.Json.Nodes;

namespace ServiceLib.Tests.Manager;

public class DetourFeaturesTests
{
    [Test]
    public async Task Update_VersionComparison()
    {
        await Assert.That(AppUpdateManager.IsNewer("v1.2.0", "1.1.9")).IsTrue();
        await Assert.That(AppUpdateManager.IsNewer("v1.2", "1.2.0")).IsFalse();
        await Assert.That(AppUpdateManager.IsNewer("v1.10.0", "1.9.3")).IsTrue();
        await Assert.That(AppUpdateManager.IsNewer("v2.0.0-beta", "1.9.0")).IsTrue();
        await Assert.That(AppUpdateManager.IsNewer("nightly", "0.1.0")).IsFalse();
    }

    [Test]
    public async Task Update_ReplaceKeepsBinAndConfigs_AndRenamesOld()
    {
        var root = Path.Combine(Path.GetTempPath(), "detour-upd-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var pkg = Path.Combine(root, "pkg", "Detour");
        Directory.CreateDirectory(Path.Combine(app, "bin"));
        Directory.CreateDirectory(Path.Combine(app, "guiConfigs"));
        Directory.CreateDirectory(Path.Combine(pkg, "bin"));
        Directory.CreateDirectory(Path.Combine(pkg, "guiConfigs"));
        File.WriteAllText(Path.Combine(app, "Detour.exe"), "old");
        File.WriteAllText(Path.Combine(app, "bin", "xray.exe"), "user core");
        File.WriteAllText(Path.Combine(app, "guiConfigs", "guiNConfig.json"), "user config");
        File.WriteAllText(Path.Combine(pkg, "Detour.exe"), "new");
        File.WriteAllText(Path.Combine(pkg, "bin", "xray.exe"), "bundled core");
        File.WriteAllText(Path.Combine(pkg, "guiConfigs", "guiNConfig.json"), "default config");
        try
        {
            var found = AppUpdateManager.FindPackageRoot(Path.Combine(root, "pkg"), "Detour.exe");
            await Assert.That(found).IsEqualTo(pkg);
            await Assert.That(AppUpdateManager.ReplaceAppFiles(pkg, app)).IsNull();
            await Assert.That(File.ReadAllText(Path.Combine(app, "Detour.exe"))).IsEqualTo("new");
            await Assert.That(File.Exists(Path.Combine(app, "Detour.exe.old"))).IsTrue();
            await Assert.That(File.ReadAllText(Path.Combine(app, "bin", "xray.exe"))).IsEqualTo("user core");
            await Assert.That(File.ReadAllText(Path.Combine(app, "guiConfigs", "guiNConfig.json"))).IsEqualTo("user config");
            AppUpdateManager.CleanupOldFiles(app);
            await Assert.That(File.Exists(Path.Combine(app, "Detour.exe.old"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Failover_PicksBestPingThenUntestedThenFailed()
    {
        var list = new List<FailoverManager.Candidate>
        {
            new("cur", 50, 1),
            new("dead", -1, 2),
            new("untested", 0, 3),
            new("slow", 400, 4),
            new("fast", 120, 5),
        };
        await Assert.That(FailoverManager.PickNext(list, "cur", [])).IsEqualTo("fast");
        await Assert.That(FailoverManager.PickNext(list, "cur", ["fast", "slow"])).IsEqualTo("untested");
        await Assert.That(FailoverManager.PickNext(list, "cur", ["fast", "slow", "untested"])).IsEqualTo("dead");
        await Assert.That(FailoverManager.PickNext(list, "cur", ["fast", "slow", "untested", "dead"])).IsNull();
    }

    [Test]
    public async Task ProviderSubscription_LinksAndUserInfo()
    {
        await Assert.That(ProviderSubscription.ExtractSubscriptionUrl("happ://add/https://sub.example.com/abc")).IsEqualTo("https://sub.example.com/abc");
        await Assert.That(ProviderSubscription.ExtractSubscriptionUrl("incy://import/https%3A%2F%2Fx.io%2Fs%2F1")).IsEqualTo("https://x.io/s/1");
        await Assert.That(ProviderSubscription.ExtractSubscriptionUrl("v2raytun://import/https://x.io/s?t=1")).IsEqualTo("https://x.io/s?t=1");
        await Assert.That(ProviderSubscription.ExtractSubscriptionUrl("happ://crypt4/AAAA")).IsNull();
        await Assert.That(ProviderSubscription.ExtractSubscriptionUrl("vless://x@y:1")).IsNull();

        var info = ProviderSubscription.ParseUserInfo("upload=1000; download=2000; total=10737418240; expire=1798761600");
        await Assert.That(info!.Used).IsEqualTo(3000);
        await Assert.That(info.Total).IsEqualTo(10737418240);
        await Assert.That(ProviderSubscription.ParseUserInfo("garbage")).IsNull();

        var (text, warn) = ServiceLib.ViewModels.ConnectionPanelViewModel.FormatSubInfo(95, 100, 0, DateTime.Now);
        await Assert.That(text).IsNotEmpty();
        await Assert.That(warn).IsTrue();
        await Assert.That(ServiceLib.ViewModels.ConnectionPanelViewModel.FormatSubInfo(0, 0, 0, DateTime.Now).Text).IsEmpty();
    }

    [Test]
    public async Task Registry_ExperimentsAfterHints()
    {
        // Windows paths and winws-only behaviour (Linux and macOS use tpws).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await Assert.That(StrategyRegistry.Experimental.All(e => ZapretDesync.Tokens(e.Desync) is not null)).IsTrue();
        await Assert.That(StrategyRegistry.Experimental.All(e => e.Name.StartsWith(ZapretDesync.RegistryPrefix))).IsTrue();

        var hint = new StrategyRegistry.Candidate("registry:abc", StrategyRegistry.Experimental[0].Desync, "note");
        var list = StrategyRegistry.WithExperiments([hint], true, _ => true);
        await Assert.That(list[0].Name).IsEqualTo("registry:abc");
        await Assert.That(list.Count).IsEqualTo(StrategyRegistry.Experimental.Count); // the duplicate experiment is dropped
        // Without the zapret release files (%BIN%stun2.bin…) the file-based experiment is skipped.
        var noFiles = StrategyRegistry.WithExperiments([], true, tokens => !tokens.Any(t => t.Contains("%BIN%")));
        await Assert.That(noFiles.Count).IsEqualTo(StrategyRegistry.Experimental.Count - 1);
        await Assert.That(StrategyRegistry.Experimental.Any(e => e.Desync.Contains("max.ru"))).IsFalse();
        await Assert.That(StrategyRegistry.WithExperiments([hint], false).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Amnezia_ConfImportKeepsObfuscation()
    {
        var conf = """
            [Interface]
            PrivateKey = cHJpdmF0ZQ==
            Address = 10.8.0.2/32
            Jc = 4
            Jmin = 40
            Jmax = 70
            S1 = 0
            S2 = 0
            H1 = 100000-200000
            H2 = 2
            I1 = <b 0xc700000001>

            [Peer]
            PublicKey = cHVibGlj
            Endpoint = 1.2.3.4:51820
            """;
        var item = WireguardFmt.ResolveConfig(conf)!.Single();
        await Assert.That(AmneziaCore.IsAmnezia(item)).IsTrue();
        var json = AmneziaCore.ToJson(item.GetProtocolExtra().WgAmnezia)!;
        await Assert.That(json["jc"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(json["h1"]!.GetValue<string>()).IsEqualTo("100000-200000");
        await Assert.That(json["h2"]!.GetValue<long>()).IsEqualTo(2);
        await Assert.That(json["i1"]!.GetValue<string>()).IsEqualTo("<b 0xc700000001>");
        await Assert.That(AppManager.Instance.GetCoreType(item, EConfigType.WireGuard)).IsEqualTo(ECoreType.sing_box);
        var endpointJson = JsonUtils.Serialize(new Endpoints4Sbox { type = "wireguard", tag = "proxy", amnezia = json });
        await Assert.That(endpointJson).Contains("\"amnezia\"");
        await Assert.That(endpointJson).Contains("\"jc\"");

        var plain = WireguardFmt.ResolveConfig("[Interface]\nPrivateKey = a\n[Peer]\nPublicKey = b\nEndpoint = 1.2.3.4:1\n")!.Single();
        await Assert.That(AmneziaCore.IsAmnezia(plain)).IsFalse();
    }

    [Test]
    public async Task ServicServers_ParseAndPick()
    {
        var rule = ConnectionRuleParser.Parse("@youtube +vpn = 🇩🇪 Германия 1", out var error);
        await Assert.That(error).IsEmpty();
        await Assert.That(rule!.Server).IsEqualTo("🇩🇪 Германия 1");
        await Assert.That(ConnectionRuleParser.Format(rule)).IsEqualTo("@youtube +vpn = 🇩🇪 Германия 1");
        await Assert.That(ConnectionRuleParser.Parse("chatgpt.com +vpn = авто", out _)!.Server).IsEqualTo("auto");
        await Assert.That(ConnectionRuleParser.Parse("youtube.com +zapret = x", out error)).IsNull();
        await Assert.That(ConnectionRuleParser.Parse("youtube.com +vpn", out _)!.Server).IsNull();

        var compiled = ConnectionRuleCompiler.Compile([rule]);
        await Assert.That(compiled.RoutingRules.All(r => r.OutboundTag == "server:🇩🇪 Германия 1")).IsTrue();

        var all = new List<ServiceServerResolver.Candidate>
        {
            new("main", "🇦🇹 Австрия 6", "s1", 80),
            new("de1", "🇩🇪 Германия 1", "s1", 120),
            new("de2", "🇩🇪 Германия 2", "s1", -1),
            new("fi", "🇫🇮 Финляндия", "s1", 40),
            new("other", "🇫🇮 Финляндия 9", "s2", 10),
        };
        await Assert.That(ServiceServerResolver.Pick("🇩🇪 Германия 1", "main", "s1", all)).IsEqualTo("de1");
        await Assert.That(ServiceServerResolver.Pick("Германия 2", "main", "s1", all)).IsEqualTo("fi"); // dead → best
        await Assert.That(ServiceServerResolver.Pick("auto", "main", "s1", all)).IsEqualTo("fi");
        await Assert.That(ServiceServerResolver.Pick("Австрия", "main", "s1", all)).IsNull(); // = main server
        await Assert.That(ServiceServerResolver.Pick("auto", "fi", "s1", all)).IsNull();

        // What the help page promises about names.
        await Assert.That(ServiceServerResolver.Pick("германия 1", "main", "s1", all)).IsEqualTo("de1"); // case, no flag
        await Assert.That(ServiceServerResolver.Pick("Финляндия", "main", "s1", all)).IsEqualTo("fi"); // same subscription first
        await Assert.That(ServiceServerResolver.Pick("Финляндия 9", "main", "s1", all)).IsEqualTo("other"); // then all
        await Assert.That(ServiceServerResolver.Pick("Германия1", "main", "s1", all)).IsEqualTo("fi"); // spaces matter: not found → best
        var unpinged = new List<ServiceServerResolver.Candidate> { new("main", "🇦🇹 Австрия 6", "s1", 0), new("de1", "🇩🇪 Германия 1", "s1", 0) };
        await Assert.That(ServiceServerResolver.Pick("Германия", "main", "s1", unpinged)).IsEqualTo("de1"); // found, no ping needed
        await Assert.That(ServiceServerResolver.Pick("Польша", "main", "s1", unpinged)).IsNull(); // not found, no pings → main
        await Assert.That(ServiceServerResolver.Pick("авто", "main", "s1", unpinged)).IsNull();
    }

    [Test]
    public async Task VpnStrategy_MuxAndXmuxPerServer()
    {
        var mux = VpnStrategyManager.Find("mux")!;
        var xmux = VpnStrategyManager.Find("xmux")!;

        var tcp = new ProfileItem { ConfigType = EConfigType.VLESS, Network = "tcp", Address = "a", Port = 443 };
        await Assert.That(VpnStrategyManager.Fits(mux, tcp)).IsTrue();
        await Assert.That(VpnStrategyManager.Fits(xmux, tcp)).IsFalse();
        await Assert.That(VpnStrategyManager.WithTransport(tcp, mux).MuxEnabled).IsEqualTo(true);
        await Assert.That(tcp.MuxEnabled).IsNotEqualTo(true); // the original is untouched

        var xhttp = new ProfileItem { ConfigType = EConfigType.VLESS, Network = "xhttp", Address = "a", Port = 443 };
        xhttp.SetTransportExtra(new TransportExtraItem { XhttpExtra = """{"noGRPCHeader":true}""" });
        var withX = VpnStrategyManager.WithTransport(xhttp, xmux).GetTransportExtra().XhttpExtra!;
        await Assert.That(withX).Contains("xmux");
        await Assert.That(withX).Contains("noGRPCHeader");

        var xmux3 = VpnStrategyManager.Find("xmux 3")!;
        await Assert.That(VpnStrategyManager.WithTransport(xhttp, xmux3).GetTransportExtra().XhttpExtra!).Contains("\"maxConnections\":3");

        var firefox = VpnStrategyManager.Find("отпечаток firefox")!;
        var reality = new ProfileItem { ConfigType = EConfigType.VLESS, Network = "tcp", Address = "a", Port = 443, StreamSecurity = "reality", Fingerprint = "chrome" };
        await Assert.That(VpnStrategyManager.Fits(firefox, reality)).IsTrue();
        await Assert.That(VpnStrategyManager.WithTransport(reality, firefox).Fingerprint).IsEqualTo("firefox");
        await Assert.That(VpnStrategyManager.Fits(VpnStrategyManager.Find("отпечаток chrome")!, reality)).IsFalse(); // already chrome
        await Assert.That(VpnStrategyManager.Fits(firefox, tcp)).IsFalse(); // no TLS: nothing to fingerprint

        // TLS record split: plain TLS only (REALITY turns tlshello into a TCP split); ordinary fragments fit anything.
        var records = VpnStrategyManager.Find("records tiny first")!;
        var tls = new ProfileItem { ConfigType = EConfigType.VLESS, Network = "ws", Address = "a", Port = 443, StreamSecurity = "tls" };
        await Assert.That(VpnStrategyManager.IsRecordSplit(records)).IsTrue();
        await Assert.That(VpnStrategyManager.Fits(records, tls)).IsTrue();
        await Assert.That(VpnStrategyManager.Fits(records, reality)).IsFalse();
        await Assert.That(VpnStrategyManager.Fits(records, tcp)).IsFalse();
        await Assert.That(VpnStrategyManager.Fits(VpnStrategyManager.Find("general")!, reality)).IsTrue();
    }

    [Test]
    public async Task ServerNetwork_FrozenHostingNetworks()
    {
        await Assert.That(ServerNetworkCheck.ParseOriginAsn("\"16276 | 51.68.0.0/16 | FR | ripencc | 2014-09-05\"")).IsEqualTo(16276);
        await Assert.That(ServerNetworkCheck.ParseOriginAsn("\"24940 213230 | 1.2.3.0/24 | DE | ripencc |\"")).IsEqualTo(24940);
        await Assert.That(ServerNetworkCheck.ParseOriginAsn(null)).IsEqualTo(0);
        await Assert.That(ServerNetworkCheck.ParseAsName("\"14061 | US | arin | 2012-09-25 | DIGITALOCEAN-ASN, US\"")).IsEqualTo("DIGITALOCEAN-ASN, US");

        await Assert.That(ServerNetworkCheck.FindFrozen(24940, "")?.Name).IsEqualTo("Hetzner");
        await Assert.That(ServerNetworkCheck.FindFrozen(99999, "AS-CHOOPA, US")?.Name).IsEqualTo("Vultr"); // by name
        await Assert.That(ServerNetworkCheck.FindFrozen(15169, "GOOGLE, US")).IsNull();

        // The published list is the built-in one.
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "strategies", "frozen-networks.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        var published = ServerNetworkCheck.ParseList(File.ReadAllText(Path.Combine(root, "strategies", "frozen-networks.json")));
        var builtin = ServerNetworkCheck.ParseList(ServerNetworkCheck.BuiltinJson());
        await Assert.That(published.Select(n => n.Name)).IsEquivalentTo(builtin.Select(n => n.Name));
        await Assert.That(ServerNetworkCheck.ParseList("""[{"Name":"x"},{"Name":"y","Patterns":["Y"]}]""").Select(n => n.Name)).IsEquivalentTo(new[] { "y" });

        // Failover: within the same country a server on a frozen network goes after the others.
        var picked = FailoverManager.PickNext(
            [new("a", 50, 0, "AT", FrozenNetwork: true), new("b", 300, 1, "AT"), new("c", 20, 2, "DE")],
            "cur", [], "AT");
        await Assert.That(picked).IsEqualTo("b");
        await Assert.That(ServerNetworkCheck.UsesUdp(new ProfileItem { ConfigType = EConfigType.Hysteria2 })).IsTrue();
    }

    [Test]
    public async Task TlsRecordSplit_CutsClientHelloIntoRecords()
    {
        // Windows paths and winws-only behaviour (Linux and macOS use tpws).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // A fake 100-byte handshake record followed by 3 bytes of something else.
        var payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        byte[] hello = [0x16, 0x03, 0x01, 0x00, 100, .. payload, 0xAA, 0xBB, 0xCC];

        var cut = TlsRecordSplitStream.Split(hello, "10-30", 0, new Random(1))!;
        var records = new List<byte[]>();
        var i = 0;
        while (i + 5 <= cut.Length && cut[i] == 0x16)
        {
            var len = (cut[i + 3] << 8) | cut[i + 4];
            await Assert.That(len).IsBetween(1, 30);
            records.Add(cut[(i + 5)..(i + 5 + len)]);
            i += 5 + len;
        }
        await Assert.That(records.Count).IsGreaterThan(3);
        await Assert.That(records.SelectMany(r => r).ToArray()).IsEquivalentTo(payload);
        await Assert.That(cut[i..]).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC });

        // First record 1–3 bytes, the rest in the second.
        var tiny = TlsRecordSplitStream.Split(hello, "1-3", 2)!;
        var first = (tiny[3] << 8) | tiny[4];
        await Assert.That(first).IsBetween(1, 3);
        await Assert.That((tiny[5 + first + 3] << 8) | tiny[5 + first + 4]).IsEqualTo(100 - first);

        // Not a handshake / incomplete record: left alone.
        await Assert.That(TlsRecordSplitStream.Split("GET / HTTP/1.1\r\n"u8, "10-30", 0)).IsNull();
        await Assert.That(TlsRecordSplitStream.Split(hello[..50], "10-30", 0)).IsNull();

        await Assert.That(ZapretManager.SiteRecords(new ZapretItem { SiteRecords = "records" })?.Name).IsEqualTo("records");
        await Assert.That(ZapretManager.SiteRecords(new ZapretItem { SiteRecords = "general" })).IsNull(); // not a record split
        await Assert.That(ZapretManager.SiteRecords(new ZapretItem())).IsNull();

        await Assert.That(VpnStrategyManager.SafeWinwsArgs("--dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com --wf-l3=x"))
            .IsEquivalentTo(new[] { "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=vk.com" });

        var config = new Config { ZapretItem = new(), CoreBasicItem = new() };
        VpnStrategyManager.Apply(config, VpnStrategyManager.Find("mux")!);
        await Assert.That(config.CoreBasicItem.EnableFragment).IsFalse();
    }

    [Test]
    public async Task Alpn_DropsH2ForWebSocket()
    {
        await Assert.That(ProfileItem.AlpnForNetwork(["h2", "http/1.1"], "ws")).IsEquivalentTo(new[] { "http/1.1" });
        await Assert.That(ProfileItem.AlpnForNetwork(["h2"], "httpupgrade")).IsEquivalentTo(new[] { "http/1.1" });
        await Assert.That(ProfileItem.AlpnForNetwork(["h2", "http/1.1"], "grpc")).IsEquivalentTo(new[] { "h2", "http/1.1" });
        await Assert.That(ProfileItem.AlpnForNetwork(null, "ws")).IsNull();
    }

    [Test]
    public async Task DiagnosticsExport_RemovesSecrets()
    {
        var json = """{"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"au6.example.org","port":443,"users":[{"id":"0b2c1d6e-1111-2222-3333-444455556666","flow":"xtls-rprx-vision"}]}]},"streamSettings":{"realitySettings":{"serverName":"www.microsoft.com","publicKey":"AbC","shortId":"12ab"}}}]}""";
        var redacted = DiagnosticsExport.RedactJson(json);
        await Assert.That(redacted).DoesNotContain("au6.example.org");
        await Assert.That(redacted).DoesNotContain("0b2c1d6e");
        await Assert.That(redacted).DoesNotContain("\"AbC\"");
        await Assert.That(redacted).Contains("xtls-rprx-vision");
        await Assert.That(redacted).Contains("443");

        var text = "import vless://abc@1.2.3.4:443?x=1#name from https://sub.example.com/sub/SeCrEt123 and https://x.io/a?token=zzz to au6.example.org, visited youtube.com";
        var t = DiagnosticsExport.RedactText(text, ["au6.example.org"]);
        await Assert.That(t).DoesNotContain("abc@1.2.3.4");
        await Assert.That(t).DoesNotContain("SeCrEt123");
        await Assert.That(t).DoesNotContain("zzz");
        await Assert.That(t).DoesNotContain("au6.example.org");
        await Assert.That(t).Contains("youtube.com");
    }

    [Test]
    public async Task GameMode_FindsKnownAndUserGames()
    {
        var known = GameModeManager.KnownGames.Concat(["MyGame.exe"]);
        await Assert.That(GameModeManager.FindGame(["explorer", "SCPSL", "chrome"], known)).IsEqualTo("SCPSL.exe");
        await Assert.That(GameModeManager.FindGame(["mygame"], known)).IsEqualTo("mygame.exe");
        await Assert.That(GameModeManager.FindGame(["explorer", "chrome"], known)).IsNull();
    }

    [Test]
    public async Task NetworkResume_SleepAndNetworkChange()
    {
        var tick = TimeSpan.FromSeconds(5);
        await Assert.That(NetworkResumeManager.IsSleepGap(tick, TimeSpan.FromSeconds(6))).IsFalse();
        await Assert.That(NetworkResumeManager.IsSleepGap(tick, TimeSpan.FromHours(5))).IsTrue();

        await Assert.That(NetworkResumeManager.ShouldReconnect(true, "wifi|1|2", "wifi|1|2")).IsTrue();
        await Assert.That(NetworkResumeManager.ShouldReconnect(false, "wifi|1|2", "wifi|1|2")).IsFalse();
        await Assert.That(NetworkResumeManager.ShouldReconnect(false, "wifi|1|2", "eth|3|4")).IsTrue();
        await Assert.That(NetworkResumeManager.ShouldReconnect(false, "wifi|1|2", null)).IsFalse();
        await Assert.That(NetworkResumeManager.ShouldReconnect(false, null, "wifi|1|2")).IsTrue();
    }

    [Test]
    public async Task Failover_PrefersSameExitCountry()
    {
        await Assert.That(ServerNameFormatter.ExitCountry("🇦🇹 КВН Австрия 6 [VL]")).IsEqualTo("AT");
        await Assert.That(ServerNameFormatter.ExitCountry("🇷🇺🇸🇪 [ДАБ] КВН ШВЕ 3")).IsEqualTo("SE");
        await Assert.That(ServerNameFormatter.ExitCountry("Server 1")).IsNull();

        var list = new List<FailoverManager.Candidate>
        {
            new("at6", 50, 1, "AT"),
            new("de-fast", 40, 2, "DE"),
            new("at2-slow", 300, 3, "AT"),
            new("at3-dead", -1, 4, "AT"),
        };
        await Assert.That(FailoverManager.PickNext(list, "at6", [], "AT")).IsEqualTo("at2-slow");
        await Assert.That(FailoverManager.PickNext(list, "at6", ["at2-slow"], "AT")).IsEqualTo("at3-dead");
        await Assert.That(FailoverManager.PickNext(list, "at6", ["at2-slow", "at3-dead"], "AT")).IsEqualTo("de-fast");
        await Assert.That(FailoverManager.PickNext(list, "at6", [])).IsEqualTo("de-fast");
    }

    [Test]
    public async Task Diagnostics_MasksAddressesHostsAndCredentials()
    {
        var masked = DiagnosticsReport.Mask("dial tcp 203.0.113.7:443 for nl-1.myvpn.example.com via vless://uuid@a.b.net:443 local 127.0.0.1:10808");
        await Assert.That(masked).DoesNotContain("203.0.113.7");
        await Assert.That(masked).DoesNotContain("myvpn");
        await Assert.That(masked).DoesNotContain("uuid@");
        await Assert.That(masked).Contains("127.0.0.1:10808");
    }

    private const string RegJson = """
        {"id":"x","config":{"client_id":"AQID","peers":[{"public_key":"bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
        "endpoint":{"host":"engage.cloudflareclient.com:2408"}}],"interface":{"addresses":{"v4":"172.16.0.2","v6":"2606:4700:110:8a36::1"}}}}
        """;

    [Test]
    public async Task Warp_ParsesRegistrationAndBuildsConfigs()
    {
        var reg = WarpManager.ParseRegistration(RegJson, "cHJpdmF0ZS1rZXktcHJpdmF0ZS1rZXktcHJpdmF0ZTE=");
        await Assert.That(reg).IsNotNull();
        await Assert.That(WarpManager.ReservedBytes(reg!.ClientId)).IsEquivalentTo(new[] { 1, 2, 3 });

        var xray = JsonNode.Parse(WarpManager.BuildXrayConfig(reg, 40000))!;
        await Assert.That(xray["inbounds"]![0]!["port"]!.GetValue<int>()).IsEqualTo(40000);
        await Assert.That(xray["inbounds"]![0]!["listen"]!.GetValue<string>()).IsEqualTo("127.0.0.1");
        var outbound = xray["outbounds"]![0]!;
        await Assert.That(outbound["protocol"]!.GetValue<string>()).IsEqualTo("wireguard");
        await Assert.That(outbound["settings"]!["peers"]![0]!["endpoint"]!.GetValue<string>()).IsEqualTo(WarpManager.DefaultEndpoint);
        await Assert.That(outbound["streamSettings"]!["finalmask"]!["udp"]![0]!["type"]!.GetValue<string>()).IsEqualTo("noise");

        var plain = JsonNode.Parse(WarpManager.BuildXrayConfig(reg, 40000, "188.114.97.1:500", noise: false))!;
        await Assert.That(plain["outbounds"]![0]!["streamSettings"]).IsNull();
        await Assert.That(plain["outbounds"]![0]!["settings"]!["peers"]![0]!["endpoint"]!.GetValue<string>()).IsEqualTo("188.114.97.1:500");
    }

    [Test]
    [Arguments("162.159.192.1:2408", true)]
    [Arguments("engage.cloudflareclient.com:2408", true)]
    [Arguments("[2606:4700:d0::a29f:c001]:2408", true)]
    [Arguments("162.159.192.1", false)]
    [Arguments("162.159.192.1:99999", false)]
    [Arguments("", false)]
    public async Task Warp_ValidatesEndpoint(string endpoint, bool valid)
    {
        await Assert.That(WarpManager.IsValidEndpoint(endpoint)).IsEqualTo(valid);
    }

    [Test]
    public async Task Warp_RulesRouteToWarpOutbound()
    {
        var rule = ConnectionRuleParser.Parse("spotify.com +варп", out var error);
        await Assert.That(error).IsEmpty();
        await Assert.That(rule!.Mode).IsEqualTo(EConnectionMode.Warp);
        await Assert.That(ConnectionRuleParser.Format(rule)).IsEqualTo("spotify.com +warp");

        var compiled = ConnectionRuleCompiler.Compile([rule]);
        await Assert.That(compiled.RoutingRules.Single().OutboundTag).IsEqualTo(Global.WarpOutboundRemarks);
        await Assert.That(compiled.ZapretExcludeDomains).Contains("spotify.com");
    }

    [Test]
    public async Task Warp_PresetCombinesBlockedServicesAndCountsAsWarpRule()
    {
        var preset = ConnectionPresets.Find("@warp");
        await Assert.That(preset).IsNotNull();
        await Assert.That(preset!.RecommendedMode).IsEqualTo(EConnectionMode.Warp);
        await Assert.That(preset.Domains).Contains("instagram.com");
        await Assert.That(preset.Domains).Contains("rutracker.org");
        await Assert.That(preset.Domains).DoesNotContain("chatgpt.com");

        var config = new Config { ConnectionRules = [new() { Target = "@youtube", Mode = EConnectionMode.Zapret }] };
        await Assert.That(WarpManager.HasWarpRules(config)).IsFalse();
        config.ConnectionRules.Add(ConnectionRuleParser.Parse("@warp +warp", out _)!);
        await Assert.That(WarpManager.HasWarpRules(config)).IsTrue();
        config.ConnectionRulesOverride = EConnectionMode.Vpn;
        await Assert.That(WarpManager.HasWarpRules(config)).IsFalse();
    }

    [Test]
    public async Task Tpws_BuildsSocksArgumentsAndFallsBackToDefault()
    {
        var args = ZapretManager.TpwsArgs("oob (midsld)", 10950);
        await Assert.That(args).Contains("--socks");
        await Assert.That(args).Contains("--bind-addr=127.0.0.1");
        await Assert.That(args).Contains("--port=10950");
        await Assert.That(args).Contains("--oob");

        var fallback = ZapretManager.TpwsArgs("general.bat", 1);
        await Assert.That(fallback).Contains("--disorder");
        await Assert.That(ZapretManager.TpwsStrategies.Select(s => s.Name).Distinct().Count()).IsEqualTo(ZapretManager.TpwsStrategies.Count);
    }

    [Test]
    public async Task Zapret_RulesUseTheGivenOutbound()
    {
        var rule = ConnectionRuleParser.Parse("youtube.com +zapret", out _)!;
        var onWindows = ConnectionRuleCompiler.Compile([rule]);
        await Assert.That(onWindows.RoutingRules.Last().OutboundTag).IsEqualTo(Global.DirectTag);

        var viaTpws = ConnectionRuleCompiler.Compile([rule], zapretOutbound: Global.ZapretOutboundRemarks);
        await Assert.That(viaTpws.RoutingRules.Last().OutboundTag).IsEqualTo(Global.ZapretOutboundRemarks);
        // QUIC is still blocked, so browsers fall back to TCP, which tpws handles.
        await Assert.That(viaTpws.RoutingRules.First().OutboundTag).IsEqualTo(Global.BlockTag);
        await Assert.That(viaTpws.ZapretDomains).Contains("youtube.com");
    }

    [Test]
    public async Task PresetLists_ParseAndMergeWithoutLosingBuiltins()
    {
        var parsed = PresetListManager.ParseList("# comment\n.ua\nDiscord.com\r\n1.2.3.4\n*.bad\n\nnew-host.example\ndiscord.com\n");
        await Assert.That(parsed).IsEquivalentTo(new[] { "ua", "discord.com", "new-host.example" });

        try
        {
            ConnectionPresets.SetExtraDomains(new Dictionary<string, IReadOnlyList<string>> { ["discord"] = ["new-host.example"] });
            var discord = ConnectionPresets.Find("@discord")!;
            await Assert.That(discord.Domains).Contains("new-host.example");
            await Assert.That(discord.Domains).Contains("discord.gg");
            await Assert.That(discord.Domains.Count(d => d == "discord.com")).IsEqualTo(1);
            await Assert.That(ConnectionPresets.Find("@blocked")).IsNotNull();
        }
        finally
        {
            ConnectionPresets.SetExtraDomains(null);
        }
        await Assert.That(ConnectionPresets.Find("@discord")!.Domains).DoesNotContain("new-host.example");
    }

    [Test]
    public async Task BlockCheck_SuggestsModesAndEditsRulesText()
    {
        var youtube = ConnectionPresets.Find("@youtube")!;
        var twitch = ConnectionPresets.Find("@twitch")!;
        var openai = ConnectionPresets.Find("@openai")!;

        await Assert.That(BlockCheckManager.Evaluate(twitch, null, true, null).Suggested).IsEqualTo(EConnectionMode.Direct);
        await Assert.That(BlockCheckManager.Evaluate(youtube, EConnectionMode.Vpn, false, true).Suggested).IsEqualTo(EConnectionMode.ZapretThenVpn);
        await Assert.That(BlockCheckManager.Evaluate(youtube, EConnectionMode.ZapretThenVpn, true, null).ChangeSuggested).IsFalse();
        await Assert.That(BlockCheckManager.Evaluate(youtube, EConnectionMode.Warp, false, false).Suggested).IsEqualTo(EConnectionMode.Warp);
        var gpt = BlockCheckManager.Evaluate(openai, null, true, null);
        await Assert.That(gpt.Suggested).IsEqualTo(EConnectionMode.Vpn);
        await Assert.That(gpt.ChangeSuggested).IsFalse();

        var text = BlockCheckManager.ApplyToText("@youtube +vpn\n@russia +direct\n",
            [BlockCheckManager.Evaluate(youtube, EConnectionMode.Vpn, false, true), BlockCheckManager.Evaluate(twitch, null, true, null)]);
        var lines = text.Split(Environment.NewLine);
        await Assert.That(lines[0]).IsEqualTo(ConnectionRuleParser.Format(new() { Target = "@youtube", Mode = EConnectionMode.ZapretThenVpn }));
        await Assert.That(lines[1]).IsEqualTo("@russia +direct");
        await Assert.That(lines[^1]).StartsWith("@twitch ");
    }

    [Test]
    public async Task VpnStrategy_DiagnosisAndApply()
    {
        var general = VpnStrategyManager.Find("general")!;
        var fine = VpnStrategyManager.Find("FINE")!;
        VpnStrategyResult R(VpnStrategy? s, bool hs, bool big, int ms = 100) => new(s, hs, big, ms);

        await Assert.That(VpnStrategyManager.Summarize(true, [R(null, true, true)]).Diagnosis).IsEqualTo(VpnDiagnosis.Works);

        var needs = VpnStrategyManager.Summarize(true, [R(null, false, false), R(general, true, true, 300), R(fine, true, true, 150)]);
        await Assert.That(needs.Diagnosis).IsEqualTo(VpnDiagnosis.NeedsStrategy);
        await Assert.That(needs.Best).IsEqualTo(fine);

        var freezeFixed = VpnStrategyManager.Summarize(true, [R(null, true, false), R(general, true, true)]);
        await Assert.That(freezeFixed.Diagnosis).IsEqualTo(VpnDiagnosis.FreezesAfterHandshake);
        await Assert.That(freezeFixed.Best).IsEqualTo(general);

        await Assert.That(VpnStrategyManager.Summarize(true, [R(null, true, false), R(general, true, false)]).Best).IsNull();
        await Assert.That(VpnStrategyManager.Summarize(false, [R(null, false, false), R(general, false, false)]).Diagnosis).IsEqualTo(VpnDiagnosis.Unreachable);
        await Assert.That(VpnStrategyManager.Summarize(true, [R(null, false, false), R(general, false, false)]).Diagnosis).IsEqualTo(VpnDiagnosis.Blocked);
        // Ping answers, TCP doesn't: the IP is blocked, not the server down.
        await Assert.That(VpnStrategyManager.Summarize(false, [R(null, false, false)], pingOk: true).Diagnosis).IsEqualTo(VpnDiagnosis.IpBlocked);
        // A frozen hosting network adds its note to a failure, never to a working server.
        var ovh = new ServerNetwork(16276, "OVH, FR", ServerNetworkCheck.FindFrozen(16276, "OVH, FR"));
        await Assert.That(VpnStrategyManager.Summarize(true, [R(null, true, false)], network: ovh).Text).Contains("AS16276");
        await Assert.That(VpnStrategyManager.Summarize(true, [R(null, true, true)], network: ovh).Text).DoesNotContain("AS16276");

        var config = new Config { ZapretItem = new(), CoreBasicItem = new() };
        VpnStrategyManager.Apply(config, fine);
        await Assert.That(config.CoreBasicItem.EnableFragment).IsTrue();
        await Assert.That(config.Fragment4RayItem!.Lengths).IsEquivalentTo(new[] { "10-30" });
        await Assert.That(config.ZapretItem.VpnStrategy).IsEqualTo("fine");
        VpnStrategyManager.Apply(config, null);
        await Assert.That(config.CoreBasicItem.EnableFragment).IsFalse();
        await Assert.That(config.ZapretItem.VpnStrategy).IsEmpty();
    }

    private const string CustomXray = """
        {
          // provider comment
          "log": {"loglevel": "debug"},
          "metrics": {"listen": "127.0.0.1:11111"},
          "inbounds": [{"tag": "socks-in", "port": 10808, "protocol": "mixed"}],
          "outbounds": [
            {"tag": "proxy", "protocol": "vless",
             "settings": {"vnext": [{"address": "vpn.example.net", "port": 8443, "users": [{"id": "x"}]}]},
             "streamSettings": {"network": "tcp", "security": "reality"}},
            {"tag": "cdn", "protocol": "trojan",
             "settings": {"servers": [{"address": "1.2.3.4", "port": 443}]},
             "streamSettings": {"security": "tls", "finalmask": {"tcp": [{"type": "own"}]}}},
            {"tag": "direct", "protocol": "freedom"}
          ],
        }
        """;

    [Test]
    public async Task CustomConfig_FragmentAndTestCopy()
    {
        var fine = VpnStrategyManager.Find("fine")!;
        await Assert.That(CustomConfigFragment.IsXray(CustomXray)).IsTrue();
        await Assert.That(CustomConfigFragment.FirstServer(CustomXray)).IsEqualTo(("vpn.example.net", 8443));

        var applied = JsonNode.Parse(CustomConfigFragment.Apply(CustomXray, fine))!;
        var mask = applied["outbounds"]![0]!["streamSettings"]!["finalmask"]!["tcp"]![0]!;
        await Assert.That(mask["type"]!.GetValue<string>()).IsEqualTo("fragment");
        // REALITY: tlshello becomes the first writes.
        await Assert.That(mask["settings"]!["packets"]!.GetValue<string>()).IsEqualTo("1-3");
        await Assert.That(mask["settings"]!["lengths"]![0]!.GetValue<string>()).IsEqualTo("10-30");
        // The provider's own mask stays.
        await Assert.That(applied["outbounds"]![1]!["streamSettings"]!["finalmask"]!["tcp"]![0]!["type"]!.GetValue<string>()).IsEqualTo("own");
        await Assert.That(applied["outbounds"]![2]!["streamSettings"]).IsNull();

        var test = JsonNode.Parse(CustomConfigFragment.BuildTestConfig(CustomXray, 23456, null)!)!;
        await Assert.That(test["inbounds"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(test["inbounds"]![0]!["port"]!.GetValue<int>()).IsEqualTo(23456);
        await Assert.That(test["inbounds"]![0]!["tag"]!.GetValue<string>()).IsEqualTo("socks-in");
        await Assert.That(test["metrics"]).IsNull();
        await Assert.That(test["outbounds"]![0]!["streamSettings"]!["finalmask"]).IsNull();

        await Assert.That(CustomConfigFragment.IsXray("""{"outbounds":[{"type":"vless","server":"a"}]}""")).IsFalse();
        await Assert.That(CustomConfigFragment.Apply("not json", fine)).IsEqualTo("not json");
    }

    [Test]
    public async Task VpnStrategy_PublishedListAndWinwsSafety()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "strategies", "vpn-strategies.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        var published = VpnStrategyManager.ParseList(File.ReadAllText(Path.Combine(root, "strategies", "vpn-strategies.json")));
        var builtin = VpnStrategyManager.ParseList(VpnStrategyManager.BuiltinJson());
        // The published file is the built-in list: keep them in sync.
        await Assert.That(published.Count).IsEqualTo(builtin.Count);
        await Assert.That(published.Select(s => s.Name)).IsEquivalentTo(builtin.Select(s => s.Name));
        await Assert.That(published.Count(s => s.UsesWinws)).IsEqualTo(7);

        var hostile = """
            [{"Name":"x","Description":"d","Packets":"","Lengths":"","Delays":"","Winws":"--dpi-desync=fake --wf-tcp=1-65535 --hostlist=C:\\evil.txt --dpi-desync-fake-tls=..\\..\\x.bin"},
             {"Name":"y","Description":"d","Packets":"tlshello","Lengths":"1; rm","Delays":"1-2"},
             {"Name":"z","Description":"d","Packets":"tlshello","Lengths":"5-9","Delays":"1-2"}]
            """;
        var parsed = VpnStrategyManager.ParseList(hostile);
        await Assert.That(parsed.Select(s => s.Name)).IsEquivalentTo(new[] { "x", "z" });
        await Assert.That(parsed[0].Winws).IsEqualTo("--dpi-desync=fake");
    }

    [Test]
    public async Task NetCheck_Bogons()
    {
        await Assert.That(NetCheckReport.IsBogon(IPAddress.Parse("10.10.34.35"))).IsTrue();
        await Assert.That(NetCheckReport.IsBogon(IPAddress.Parse("198.18.0.5"))).IsTrue();
        await Assert.That(NetCheckReport.IsBogon(IPAddress.Parse("104.16.1.1"))).IsFalse();
        var frozen = NetCheckReport.Format(new(true, true, 16_000, 7000, "HTTP/1.1 200 OK", "stalled", true));
        await Assert.That(frozen).Contains("16");
    }

    [Test]
    public async Task Warp_ParsesTrace()
    {
        var trace = WarpProxyManager.ParseTrace("fl=1\nip=104.28.1.1\ncolo=WAW\nloc=PL\nwarp=on\n");
        await Assert.That(trace["warp"]).IsEqualTo("on");
        await Assert.That(trace["colo"]).IsEqualTo("WAW");
    }

    [Test]
    public async Task Warp_ParsesSingboxKeypairOutput()
    {
        var keys = WarpManager.ParseKeyPair("PrivateKey: aaa=\r\nPublicKey: bbb=\r\n");
        await Assert.That(keys).IsNotNull();
        await Assert.That(keys!.Value.Private).IsEqualTo("aaa=");
        await Assert.That(keys.Value.Public).IsEqualTo("bbb=");
    }

    [Test]
    public async Task ZapretHealer_StuckRulesAndVersions()
    {
        var start = new DateTime(2026, 11, 16, 12, 0, 0);
        List<ConnectionRule> rules = [new() { Target = "discord.com", Mode = EConnectionMode.ZapretThenVpn, FallbackActive = true }];
        await Assert.That(ZapretHealer.ShouldHeal(rules, start.AddMinutes(5), start, null)).IsFalse();
        await Assert.That(ZapretHealer.ShouldHeal(rules, start.AddMinutes(11), start, null)).IsTrue();
        await Assert.That(ZapretHealer.ShouldHeal(rules, start.AddMinutes(11), start, start.AddHours(-1))).IsFalse();
        rules[0].FallbackActive = false;
        await Assert.That(ZapretHealer.ShouldHeal(rules, start.AddHours(1), start, null)).IsFalse();

        await Assert.That(ZapretHealer.IsNewer("1.9.1", "1.8.5")).IsTrue();
        await Assert.That(ZapretHealer.IsNewer("1.9.1", "1.9.1")).IsFalse();
        await Assert.That(ZapretHealer.IsNewer("1.10.0", "1.9.9b")).IsTrue();
        await Assert.That(ZapretHealer.IsNewer(null, "1.9.1")).IsFalse();
    }

    [Test]
    public async Task SiteCheck_NormalizeAndEvaluate()
    {
        await Assert.That(SiteCheckManager.NormalizeHost("https://www.RuTracker.org/forum/index.php")).IsEqualTo("rutracker.org");
        await Assert.That(SiteCheckManager.NormalizeHost("x.com")).IsEqualTo("x.com");
        await Assert.That(SiteCheckManager.NormalizeHost("hello")).IsNull();
        await Assert.That(SiteCheckManager.NormalizeHost("1.2.3.4")).IsNull();

        List<ConnectionRule> rules = [new() { Target = "@youtube", Mode = EConnectionMode.Zapret }, new() { Target = "site.org", Mode = EConnectionMode.Direct }];
        await Assert.That(SiteCheckManager.FindRule(rules, "www.youtube.com")?.Target).IsEqualTo("@youtube");
        await Assert.That(SiteCheckManager.FindRule(rules, "a.site.org")?.Target).IsEqualTo("site.org");
        await Assert.That(SiteCheckManager.FindRule(rules, "other.org")).IsNull();

        const int none = SiteCheckManager.NoData;
        var direct = SiteCheckManager.Evaluate("a.ru", null, 200, null, null);
        await Assert.That(direct.Suggested).IsEqualTo(EConnectionMode.Direct);
        await Assert.That(direct.ChangeSuggested).IsTrue();
        var zapret = SiteCheckManager.Evaluate("a.ru", null, none, 200, null);
        await Assert.That(zapret.Suggested).IsEqualTo(EConnectionMode.ZapretThenVpn);
        var vpn = SiteCheckManager.Evaluate("a.ru", null, none, none, 200);
        await Assert.That(vpn.Suggested).IsEqualTo(EConnectionMode.Vpn);
        await Assert.That(vpn.ChangeSuggested).IsFalse();
        var geo = SiteCheckManager.Evaluate("a.ru", EConnectionMode.Direct, 403, null, 200);
        await Assert.That(geo.Suggested).IsEqualTo(EConnectionMode.Vpn);
        await Assert.That(geo.ChangeSuggested).IsTrue();
        var down = SiteCheckManager.Evaluate("a.ru", null, none, none, none);
        await Assert.That(down.Suggested).IsNull();
        await Assert.That(down.ChangeSuggested).IsFalse();

        var text = BlockCheckManager.SetRuleLine("a.ru direct\r\nb.ru vpn", "b.ru", EConnectionMode.Direct);
        await Assert.That(text.Split(Environment.NewLine).Length).IsEqualTo(2);
        await Assert.That(text).DoesNotContain("b.ru vpn");
    }

    [Test]
    public async Task TrafficSplit_SpreadsUnseenBytes()
    {
        static ConnectionItem C(string id, ulong bytes, string tag, string host) =>
            new() { id = id, download = bytes, chains = [tag], metadata = new() { host = host } };
        static bool Zapret(string h) => TrafficSplitManager.IsUnder(h, new HashSet<string> { "youtube.com" });

        var tracker = new TrafficSplitTracker();
        var first = tracker.Update(new() { downloadTotal = 1000, connections = [C("1", 500, "proxy", "a.com")] }, Zapret);
        await Assert.That(first.Vpn + first.Direct + first.Zapret).IsEqualTo(0L);

        // +300 VPN, +600 zapret seen; 100 unseen bytes spread 1:2.
        var second = tracker.Update(new()
        {
            downloadTotal = 2000,
            connections = [C("1", 800, "proxy", "a.com"), C("2", 600, "direct", "rr1.googlevideo.youtube.com")],
        }, Zapret);
        await Assert.That(second.Vpn).IsEqualTo(333L);
        await Assert.That(second.Zapret).IsEqualTo(666L);
        await Assert.That(second.Direct).IsEqualTo(0L);

        var block = TrafficSplitTracker.Classify(C("3", 1, "block", "x"), Zapret);
        await Assert.That(block).IsEqualTo(TrafficSplitTracker.Path.None);

        var stats = new TrafficSplitStats();
        stats.Add(10, 20, 30, new DateTime(2026, 11, 15, 23, 0, 0));
        stats.Add(1, 2, 3, new DateTime(2026, 11, 16, 1, 0, 0));
        await Assert.That(stats.TodayZapret).IsEqualTo(3L);
        await Assert.That(stats.TotalZapret).IsEqualTo(33L);
    }
}