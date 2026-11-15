namespace ServiceLib.Tests.Manager;

public class ZapretWatchdogTests
{
    private sealed class Fake
    {
        public bool Works;
        public HashSet<string> Broken = [];
        public List<string> Probed = [];
        public ZapretWatchdog Watchdog;

        public Fake()
        {
            Watchdog = new ZapretWatchdog((url, _) =>
            {
                lock (Probed)
                {
                    Probed.Add(url);
                }
                return Task.FromResult(Works && !Broken.Contains(url));
            }, () => new DateTime(2026, 1, 1));
        }

        public async Task Rounds(List<ConnectionRule> rules, int n)
        {
            for (var i = 0; i < n; i++)
            {
                await Watchdog.CheckOnceAsync(rules);
            }
        }
    }

    private static ConnectionRule Discord(bool onVpn = true) =>
        new() { Target = "@discord", Mode = EConnectionMode.ZapretThenVpn, FallbackActive = onVpn };

    [Test]
    public async Task ResetToVpn_StartsZapretThenVpnRulesOnVpn()
    {
        var rules = new List<ConnectionRule>
        {
            new() { Target = "@discord", Mode = EConnectionMode.ZapretThenVpn },
            new() { Target = "x.com", Mode = EConnectionMode.Zapret },
        };

        ZapretWatchdog.ResetToVpn(rules);

        await rules[0].FallbackActive.Should().BeTrue();
        await rules[1].FallbackActive.Should().BeFalse();
        await ConnectionRuleCompiler.EffectiveMode(rules[0]).Should().BeEqualTo(EConnectionMode.Vpn);
    }

    [Test]
    public async Task OnVpn_MovesToZapretAfterSuccessesInARow()
    {
        var f = new Fake { Works = true };
        var rules = new List<ConnectionRule> { Discord() };

        await f.Rounds(rules, ZapretWatchdog.SuccessesToUseZapret - 1);
        await rules[0].FallbackActive.Should().BeTrue();
        var msgs = await f.Watchdog.CheckOnceAsync(rules);

        await msgs.Should().HaveCount(1);
        await rules[0].FallbackActive.Should().BeFalse();
        await f.Probed.Should().Contain("https://discord.com/");
        await f.Probed.Should().Contain("https://gateway.discord.gg/");
    }

    [Test]
    public async Task OnVpn_OneBrokenHostKeepsVpn()
    {
        var f = new Fake { Works = true };
        f.Broken.Add("https://gateway.discord.gg/");
        var rules = new List<ConnectionRule> { Discord() };

        await f.Rounds(rules, 5);

        await rules[0].FallbackActive.Should().BeTrue();
    }

    [Test]
    public async Task OnZapret_FallsBackToVpnAfterFailuresInARow()
    {
        var f = new Fake { Works = false };
        var rules = new List<ConnectionRule> { Discord(onVpn: false) };

        await f.Rounds(rules, ZapretWatchdog.FailuresToFallback - 1);
        await rules[0].FallbackActive.Should().BeFalse();
        var msgs = await f.Watchdog.CheckOnceAsync(rules);

        await msgs.Should().HaveCount(1);
        await rules[0].FallbackActive.Should().BeTrue();
        await rules[0].FallbackCount.Should().BeEqualTo(1);
    }

    [Test]
    public async Task OnZapret_SingleGlitchDoesNotSwitch()
    {
        var f = new Fake { Works = true };
        var rules = new List<ConnectionRule> { Discord(onVpn: false) };

        for (var round = 0; round < 3; round++)
        {
            f.Works = false;
            await f.Rounds(rules, ZapretWatchdog.FailuresToFallback - 1);
            f.Works = true;
            await f.Watchdog.CheckOnceAsync(rules);
        }

        await rules[0].FallbackActive.Should().BeFalse();
    }

    [Test]
    public async Task IgnoresOtherModesDisabledRulesAndProcesses()
    {
        var f = new Fake();
        var rules = new List<ConnectionRule>
        {
            new() { Target = "youtube.com", Mode = EConnectionMode.Zapret },
            new() { Target = "x.com", Mode = EConnectionMode.ZapretThenVpn, Enabled = false },
            new() { Target = "game.exe", Mode = EConnectionMode.ZapretThenVpn },
        };
        await f.Rounds(rules, 5);

        await f.Probed.Should().BeEmpty();
    }

    [Test]
    public async Task ParseDohAnswer_TakesARecords_SkipsCnameAndFakeIp()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"www.youtube.com.","type":5,"data":"youtube-ui.l.google.com."},
              {"name":"youtube-ui.l.google.com.","type":1,"data":"142.251.157.4"},
              {"name":"youtube-ui.l.google.com.","type":1,"data":"198.18.0.7"}
            ]}
            """;

        var ips = ZapretWatchdog.ParseDohAnswer(json);

        await ips.Length.Should().BeEqualTo(1);
        await ips[0].ToString().Should().BeEqualTo("142.251.157.4");
        await ZapretWatchdog.ParseDohAnswer("garbage").Length.Should().BeEqualTo(0);
        await ZapretWatchdog.ParseDohAnswer("""{"Status":3}""").Length.Should().BeEqualTo(0);
    }

    [Test]
    public async Task ZapretUnavailable_ZapretThenVpnGoesViaVpn_PureZapretStaysDirect()
    {
        var rules = new List<ConnectionRule>
        {
            new() { Target = "@youtube", Mode = EConnectionMode.ZapretThenVpn },
            new() { Target = "rutracker.org", Mode = EConnectionMode.Zapret },
        };

        var c = ConnectionRuleCompiler.Compile(rules, null, zapretAvailable: false);

        await c.RoutingRules.Where(r => r.Domain!.Contains("domain:youtube.com")).All(r => r.OutboundTag == Global.ProxyTag).Should().BeTrue();
        await c.RoutingRules.Last(r => r.Domain!.Contains("domain:rutracker.org")).OutboundTag.Should().BeEqualTo(Global.DirectTag);
        await c.ZapretDomains.Should().NotContain("youtube.com");
    }

    [Test]
    public async Task NewZapretThenVpnRuleFromEditor_StartsOnVpn()
    {
        var config = new Config { ConnectionRules = [] };

        ConnectionRulesManager.ReplaceFromText(config, "@discord +zapret+vpn");

        await config.ConnectionRules[0].FallbackActive.Should().BeTrue();
    }
}
