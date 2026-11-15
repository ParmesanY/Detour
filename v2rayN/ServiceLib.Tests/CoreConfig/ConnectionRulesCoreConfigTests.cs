namespace ServiceLib.Tests.CoreConfig;

/// <summary>Connection rules end up in the generated sing-box and Xray configs, before the regular routing.</summary>
public class ConnectionRulesCoreConfigTests
{
    private static List<ConnectionRule> Rules() =>
    [
        new() { Target = "Discord.exe", Mode = EConnectionMode.Zapret },
        new() { Target = "youtube.com", Mode = EConnectionMode.Zapret },
        new() { Target = "chatgpt.com", Mode = EConnectionMode.Vpn },
        new() { Target = "ads.example.com", Mode = EConnectionMode.Block },
    ];

    private static CoreConfigContext Context(ECoreType coreType)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.ConnectionRules = Rules();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(coreType, "n-main", "main");
        var context = CoreConfigTestFactory.CreateContext(config, node, coreType);
        var regular = new RoutingItem
        {
            Id = "r1",
            Remarks = "regular",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Enabled = true, RuleType = ERuleType.Routing, OutboundTag = Global.ProxyTag, Domain = ["full:youtube.com"] },
            }),
            DomainStrategy = Global.AsIs,
            DomainStrategy4Singbox = string.Empty,
        };
        return context with { RoutingItem = ConnectionRulesManager.Instance.ApplyToRouting(config, regular) };
    }

    [Test]
    public async Task Singbox_ContainsConnectionRulesFirst()
    {
        var result = new CoreConfigSingboxService(Context(ECoreType.sing_box)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because(result.Msg);
        var rules = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!.route.rules;

        var processIdx = rules.FindIndex(r => r.process_name?.Contains("Discord.exe") == true && r.outbound == Global.DirectTag);
        var zapretIdx = rules.FindIndex(r => r.domain_suffix?.Contains("youtube.com") == true && r.outbound == Global.DirectTag);
        var vpnIdx = rules.FindIndex(r => r.domain_suffix?.Contains("chatgpt.com") == true && r.outbound == Global.ProxyTag);
        var blockIdx = rules.FindIndex(r => r.domain_suffix?.Contains("ads.example.com") == true && r.action == "reject");
        var regularIdx = rules.FindIndex(r => r.domain?.Contains("youtube.com") == true && r.outbound == Global.ProxyTag);

        await processIdx.Should().BeGreaterThanOrEqualTo(0);
        await zapretIdx.Should().BeGreaterThanOrEqualTo(0);
        await vpnIdx.Should().BeGreaterThanOrEqualTo(0);
        await blockIdx.Should().BeGreaterThanOrEqualTo(0);
        await regularIdx.Should().BeGreaterThanOrEqualTo(0);
        // Our zapret rule must win over the regular rule that sends youtube.com to the proxy.
        await (zapretIdx < regularIdx).Should().BeTrue();
    }

    [Test]
    public async Task Xray_ContainsConnectionRulesFirst()
    {
        var result = new CoreConfigV2rayService(Context(ECoreType.Xray)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because(result.Msg);
        var json = result.Data!.ToString()!;
        var rules = JsonUtils.Deserialize<V2rayConfig>(json)!.routing.rules;

        var zapretIdx = rules.FindIndex(r => r.domain?.Contains("domain:youtube.com") == true && r.outboundTag == Global.DirectTag);
        var vpnIdx = rules.FindIndex(r => r.domain?.Contains("domain:chatgpt.com") == true && r.outboundTag == Global.ProxyTag);
        var regularIdx = rules.FindIndex(r => r.domain?.Contains("full:youtube.com") == true);

        await zapretIdx.Should().BeGreaterThanOrEqualTo(0);
        await vpnIdx.Should().BeGreaterThanOrEqualTo(0);
        await (zapretIdx < regularIdx).Should().BeTrue();
    }
}
