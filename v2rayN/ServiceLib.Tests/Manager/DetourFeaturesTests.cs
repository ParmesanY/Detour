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
}
