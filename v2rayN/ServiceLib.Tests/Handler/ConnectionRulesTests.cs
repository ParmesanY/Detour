namespace ServiceLib.Tests.Handler;

public class ConnectionRuleParserTests
{
    [Test]
    [Arguments("Discord.exe +zapret+vpn", "Discord.exe", EConnectionMode.ZapretThenVpn)]
    [Arguments("Discord.exe +zapret +vpn", "Discord.exe", EConnectionMode.ZapretThenVpn)]
    [Arguments("discord.exe +vpn+zapret", "discord.exe", EConnectionMode.ZapretThenVpn)]
    [Arguments("youtube.com +zapret", "youtube.com", EConnectionMode.Zapret)]
    [Arguments("*.youtube.com +запрет", "youtube.com", EConnectionMode.Zapret)]
    [Arguments(".chatgpt.com +впн", "chatgpt.com", EConnectionMode.Vpn)]
    [Arguments("https://claude.ai/new +vpn", "claude.ai", EConnectionMode.Vpn)]
    [Arguments("1.2.3.0/24 +direct", "1.2.3.0/24", EConnectionMode.Direct)]
    [Arguments("ads.example.com +блок", "ads.example.com", EConnectionMode.Block)]
    [Arguments("@telegram +vpn", "@telegram", EConnectionMode.Vpn)]
    [Arguments("\"C:\\Program Files\\My App\\app.exe\" +напрямую", "C:\\Program Files\\My App\\app.exe", EConnectionMode.Direct)]
    public async Task Parse_ValidLines(string line, string target, EConnectionMode mode)
    {
        var rule = ConnectionRuleParser.Parse(line, out var error);

        await rule.Should().NotBeNull().Because(error);
        await rule!.Target.Should().BeEqualTo(target);
        await rule.Mode.Should().BeEqualTo(mode);
    }

    [Test]
    [Arguments("discord.com", "no mode")]
    [Arguments("discord.com +vpn+direct", "cannot be combined")]
    [Arguments("discord.com +fast", "unknown mode")]
    [Arguments("@nosuchpreset +vpn", "unknown preset")]
    [Arguments("+vpn", "no mode")]
    public async Task Parse_InvalidLines(string line, string errorPart)
    {
        var rule = ConnectionRuleParser.Parse(line, out var error);

        await rule.Should().BeNull();
        await error.Should().Contain(errorPart);
    }

    [Test]
    [Arguments("Discord.exe", EConnectionTarget.Process)]
    [Arguments(@"C:\Games\game.exe", EConnectionTarget.Process)]
    [Arguments("discord.com", EConnectionTarget.Domain)]
    [Arguments("geosite:youtube", EConnectionTarget.Domain)]
    [Arguments("10.0.0.1", EConnectionTarget.Ip)]
    [Arguments("2001:db8::/32", EConnectionTarget.Ip)]
    [Arguments("geoip:ru", EConnectionTarget.Ip)]
    [Arguments("@discord", EConnectionTarget.Preset)]
    [Arguments("1.2", EConnectionTarget.Domain)]
    public async Task GetTargetKind(string target, EConnectionTarget kind)
    {
        await ConnectionRuleParser.GetTargetKind(target).Should().BeEqualTo(kind);
    }

    [Test]
    public async Task Format_RoundTrips()
    {
        foreach (var line in new[] { "Discord.exe +zapret+vpn", "youtube.com +zapret", "\"C:\\A B\\x.exe\" +direct", "@steam +block" })
        {
            var rule = ConnectionRuleParser.Parse(line, out _)!;
            var again = ConnectionRuleParser.Parse(ConnectionRuleParser.Format(rule), out var error);
            await again.Should().NotBeNull().Because(error);
            await again!.Target.Should().BeEqualTo(rule.Target);
            await again.Mode.Should().BeEqualTo(rule.Mode);
        }
    }
}

public class ConnectionRuleCompilerTests
{
    private static ConnectionRule R(string target, EConnectionMode mode, bool fallback = false, bool enabled = true)
        => new() { Target = target, Mode = mode, FallbackActive = fallback, Enabled = enabled };

    [Test]
    public async Task Zapret_DomainGoesDirectAndToHostlist()
    {
        var c = ConnectionRuleCompiler.Compile([R("youtube.com", EConnectionMode.Zapret)]);

        // QUIC is blocked first, then the domain goes direct.
        await c.RoutingRules.Should().HaveCount(2);
        await c.RoutingRules[0].OutboundTag.Should().BeEqualTo(Global.BlockTag);
        await c.RoutingRules[0].Network.Should().BeEqualTo("udp");
        await c.RoutingRules[0].Port.Should().BeEqualTo("443");
        await c.RoutingRules[1].OutboundTag.Should().BeEqualTo(Global.DirectTag);
        await c.RoutingRules[1].Domain![0].Should().BeEqualTo("domain:youtube.com");
        await c.RoutingRules[1].RuleType.Should().BeEqualTo(ERuleType.Routing);
        await c.ZapretDomains.Should().Contain("youtube.com");
    }

    [Test]
    public async Task ZapretThenVpn_UsesZapretUntilFallback()
    {
        var before = ConnectionRuleCompiler.Compile([R("@discord", EConnectionMode.ZapretThenVpn)]);
        var after = ConnectionRuleCompiler.Compile([R("@discord", EConnectionMode.ZapretThenVpn, fallback: true)]);

        await before.RoutingRules.All(r => r.OutboundTag == Global.DirectTag || r is { OutboundTag: Global.BlockTag, Port: "443" }).Should().BeTrue();
        await before.ZapretDomains.Should().Contain("discord.com");

        await after.RoutingRules.All(r => r.OutboundTag == Global.ProxyTag).Should().BeTrue();
        // Still handed to winws while on VPN, so the watchdog's direct check can pass.
        await after.ZapretDomains.Should().Contain("discord.com");
        await after.ZapretExcludeDomains.Should().NotContain("discord.com");
    }

    [Test]
    public async Task ZapretThenVpn_UnderVpnOverride_NotInZapretLists()
    {
        var c = ConnectionRuleCompiler.Compile([R("@discord", EConnectionMode.ZapretThenVpn, fallback: true)], EConnectionMode.Vpn);

        await c.ZapretDomains.Should().BeEmpty();
    }

    [Test]
    public async Task Preset_SplitsIntoProcessDomainAndIpRules()
    {
        var c = ConnectionRuleCompiler.Compile([R("@telegram", EConnectionMode.Vpn)]);

        await c.RoutingRules.Should().HaveCount(3);
        await c.RoutingRules.Count(r => r.Process?.Count > 0).Should().BeEqualTo(1);
        await c.RoutingRules.Count(r => r.Domain?.Count > 0).Should().BeEqualTo(1);
        await c.RoutingRules.Count(r => r.Ip?.Count > 0).Should().BeEqualTo(1);
        await c.RoutingRules.All(r => r.OutboundTag == Global.ProxyTag && r.RuleType == ERuleType.ALL).Should().BeTrue();
    }

    [Test]
    public async Task Zapret_ProcessOnly_GoesDirectWithWarning()
    {
        var c = ConnectionRuleCompiler.Compile([R("game.exe", EConnectionMode.Zapret)]);

        await c.RoutingRules.Should().HaveCount(1);
        await c.RoutingRules[0].Process![0].Should().BeEqualTo("game.exe");
        await c.ZapretDomains.Should().BeEmpty();
        await c.Warnings.Should().HaveCount(1);
    }

    [Test]
    public async Task Zapret_GeositeIsRoutedButNotPassedToZapret()
    {
        var c = ConnectionRuleCompiler.Compile([R("geosite:youtube", EConnectionMode.Zapret)]);

        await c.RoutingRules[^1].Domain![0].Should().BeEqualTo("geosite:youtube");
        await c.ZapretDomains.Should().BeEmpty();
        await c.Warnings.Should().HaveCount(1);
    }

    [Test]
    public async Task DisabledRules_AreSkipped_AndOrderIsKept()
    {
        var c = ConnectionRuleCompiler.Compile([
            R("a.com", EConnectionMode.Vpn),
            R("b.com", EConnectionMode.Direct, enabled: false),
            R("c.com", EConnectionMode.Block),
        ]);

        await c.RoutingRules.Should().HaveCount(2);
        await c.RoutingRules[0].Domain![0].Should().BeEqualTo("domain:a.com");
        await c.RoutingRules[1].OutboundTag.Should().BeEqualTo(Global.BlockTag);
    }

    [Test]
    public async Task EarlierZapretRule_WinsOverLaterExclude()
    {
        var c = ConnectionRuleCompiler.Compile([
            R("x.com", EConnectionMode.Zapret),
            R("x.com", EConnectionMode.Vpn),
        ]);

        await c.ZapretDomains.Should().Contain("x.com");
        await c.ZapretExcludeDomains.Should().NotContain("x.com");
    }

    [Test]
    public async Task ApplyToRouting_PrependsWithoutTouchingOriginal()
    {
        var original = new RoutingItem
        {
            Id = "r1",
            RuleSet = JsonUtils.Serialize(new List<RulesItem> { new() { Id = "old", OutboundTag = Global.DirectTag, Domain = ["geosite:cn"] } }, false),
        };
        var originalJson = original.RuleSet;
        var compiled = ConnectionRuleCompiler.Compile([R("youtube.com", EConnectionMode.Vpn)]);

        var applied = ConnectionRuleCompiler.ApplyToRouting(original, compiled)!;
        var rules = JsonUtils.Deserialize<List<RulesItem>>(applied.RuleSet)!;

        await original.RuleSet.Should().BeEqualTo(originalJson);
        await rules.Should().HaveCount(2);
        await rules[0].Domain![0].Should().BeEqualTo("domain:youtube.com");
        await rules[1].Id.Should().BeEqualTo("old");
    }

    [Test]
    public async Task AllPresets_Compile()
    {
        foreach (var preset in ConnectionPresets.All)
        {
            var c = ConnectionRuleCompiler.Compile([R(preset.Target, preset.RecommendedMode)]);
            await c.RoutingRules.Should().NotBeEmpty().Because(preset.Id);
        }
    }
}

public class ConnectionRulesOverrideAndProfileTests
{
    [Test]
    public async Task Override_SendsAllButBlockThroughForcedMode()
    {
        var rules = new List<ConnectionRule>
        {
            new() { Target = "a.com", Mode = EConnectionMode.Zapret },
            new() { Target = "b.com", Mode = EConnectionMode.Direct },
            new() { Target = "c.com", Mode = EConnectionMode.Block },
        };

        var c = ConnectionRuleCompiler.Compile(rules, EConnectionMode.Vpn);

        await c.RoutingRules[0].OutboundTag.Should().BeEqualTo(Global.ProxyTag);
        await c.RoutingRules[1].OutboundTag.Should().BeEqualTo(Global.ProxyTag);
        await c.RoutingRules[2].OutboundTag.Should().BeEqualTo(Global.BlockTag);
        await c.ZapretDomains.Should().BeEmpty();
    }

    [Test]
    public async Task Override_ZapretFeedsAllDomainsToZapret()
    {
        var c = ConnectionRuleCompiler.Compile([new() { Target = "x.com", Mode = EConnectionMode.Vpn }], EConnectionMode.Zapret);

        await c.RoutingRules[^1].OutboundTag.Should().BeEqualTo(Global.DirectTag);
        await c.ZapretDomains.Should().Contain("x.com");
    }

    [Test]
    public async Task Profiles_SaveLoadDelete_AreIndependentCopies()
    {
        var config = new Config { ConnectionRules = [new() { Target = "a.com", Mode = EConnectionMode.Vpn }], ConnectionRuleProfiles = [] };

        ConnectionRulesManager.SaveProfile(config, "Дом");
        config.ConnectionRules[0].Mode = EConnectionMode.Direct;
        ConnectionRulesManager.SaveProfile(config, "дом");

        await config.ConnectionRuleProfiles.Should().HaveCount(1);
        config.ConnectionRules = [];
        await ConnectionRulesManager.LoadProfile(config, "ДОМ").Should().BeTrue();
        await config.ConnectionRules[0].Mode.Should().BeEqualTo(EConnectionMode.Direct);

        config.ConnectionRules[0].Mode = EConnectionMode.Block;
        await config.ConnectionRuleProfiles[0].Rules[0].Mode.Should().BeEqualTo(EConnectionMode.Direct);

        ConnectionRulesManager.DeleteProfile(config, "дом");
        await config.ConnectionRuleProfiles.Should().BeEmpty();
        await ConnectionRulesManager.LoadProfile(config, "дом").Should().BeFalse();
    }
}

public class ConnectionRulesTextTests
{
    [Test]
    public async Task ReplaceFromText_ParsesAndKeepsFallbackState()
    {
        var config = new Config
        {
            ConnectionRules = [new() { Target = "@discord", Mode = EConnectionMode.ZapretThenVpn, FallbackActive = true }],
        };

        var errors = ConnectionRulesManager.ReplaceFromText(config, """
            # my rules
            @discord +zapret+vpn
            # chatgpt.com +vpn

            youtube.com +zapret
            """);

        await errors.Should().BeEmpty();
        await config.ConnectionRules.Should().HaveCount(3);
        await config.ConnectionRules[0].FallbackActive.Should().BeTrue();
        await config.ConnectionRules[1].Enabled.Should().BeFalse();
        await config.ConnectionRules[1].Target.Should().BeEqualTo("chatgpt.com");
    }

    [Test]
    public async Task ReplaceFromText_ErrorsKeepOldRules()
    {
        var config = new Config { ConnectionRules = [new() { Target = "a.com", Mode = EConnectionMode.Vpn }] };

        var errors = ConnectionRulesManager.ReplaceFromText(config, "b.com +vpn\nc.com +nonsense");

        await errors.Should().HaveCount(1);
        await errors[0].Should().StartWith("2:");
        await config.ConnectionRules.Should().HaveCount(1);
        await config.ConnectionRules[0].Target.Should().BeEqualTo("a.com");
    }

    [Test]
    public async Task ToText_RoundTrips()
    {
        var config = new Config { ConnectionRules = ConnectionPresets.DefaultRules() };
        config.ConnectionRules[1].Enabled = false;
        var text = ConnectionRulesManager.ToText(config);

        var copy = new Config { ConnectionRules = [] };
        var errors = ConnectionRulesManager.ReplaceFromText(copy, text);

        await errors.Should().BeEmpty();
        await copy.ConnectionRules.Should().HaveCount(config.ConnectionRules.Count);
        await copy.ConnectionRules[1].Enabled.Should().BeFalse();
    }
}
