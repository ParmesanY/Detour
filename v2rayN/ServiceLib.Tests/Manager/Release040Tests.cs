namespace ServiceLib.Tests.Manager;

public class Release040Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);
    private static StallWatchManager.Sample S(string id, long up, long down, bool vpn = true) => new(id, up, down, vpn);

    [Test]
    public async Task StallWatch_ClosesWhenAppTalksButNothingComesBack()
    {
        var d = new StallWatchManager.Detector();
        await Assert.That(d.Observe(T0, [S("a", 1000, 50_000)])).IsEmpty();
        await Assert.That(d.Observe(T0.AddSeconds(5), [S("a", 1200, 50_000)])).IsEmpty();
        await Assert.That(d.Observe(T0.AddSeconds(15), [S("a", 1400, 50_000)])).IsEmpty();
        var closed = d.Observe(T0.AddSeconds(25), [S("a", 1600, 50_000)]);
        await Assert.That(closed.Select(c => c.Id)).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task StallWatch_IgnoresIdleUploadsAndDirect()
    {
        var d = new StallWatchManager.Detector();
        // idle: nothing sent either
        d.Observe(T0, [S("idle", 1000, 50_000), S("up", 0, 50_000), S("dir", 1000, 50_000, vpn: false)]);
        d.Observe(T0.AddSeconds(10), [S("idle", 1000, 50_000), S("up", 500_000, 50_000), S("dir", 1200, 50_000, vpn: false)]);
        var closed = d.Observe(T0.AddSeconds(25), [S("idle", 1000, 50_000), S("up", 900_000, 50_000), S("dir", 1400, 50_000, vpn: false)]);
        await Assert.That(closed).IsEmpty();
    }

    [Test]
    public async Task StallWatch_DataComingBack_NotStalled()
    {
        var d = new StallWatchManager.Detector();
        d.Observe(T0, [S("a", 1000, 50_000)]);
        d.Observe(T0.AddSeconds(15), [S("a", 1200, 50_100)]);
        await Assert.That(d.Observe(T0.AddSeconds(25), [S("a", 1400, 50_100)])).IsEmpty(); // 10 s since data
    }

    [Test]
    public async Task PickNext_SameHostFirst_ThenOtherNetwork()
    {
        var candidates = new List<FailoverManager.Candidate>
        {
            new("cur", 100, 0, "DE"),
            new("de-same-as", 50, 1, "DE", SameNetwork: true),
            new("de-other-as", 300, 2, "DE"),
            new("cur-xhttp", 900, 3, "DE", SameHost: true),
        };
        await Assert.That(FailoverManager.PickNext(candidates, "cur", [], "DE")).IsEqualTo("cur-xhttp");
        await Assert.That(FailoverManager.PickNext(candidates, "cur", ["cur-xhttp"], "DE")).IsEqualTo("de-other-as");
    }

    [Test]
    public async Task ProxyHost_OfCustomConfig()
    {
        const string json = """
            { "outbounds": [ { "tag": "direct", "protocol": "freedom" },
              { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "de.example.com", "port": 443 } ] } } ] }
            """;
        await Assert.That(CustomConfigRuleServers.ProxyHost(json)).IsEqualTo("de.example.com");
        await Assert.That(CustomConfigRuleServers.ProxyHost("{}")).IsNull();
    }

    [Test]
    public async Task FormatRuleServers_Lines()
    {
        var text = ServiceLib.ViewModels.ConnectionPanelViewModel.FormatRuleServers(
            [new("A; B", "B", "A"), new("C; D", "C", null)], l => l == "A; B" ? "@youtube" : "AyuGram.exe");
        await Assert.That(text.Split('\n').Length).IsEqualTo(2);
        await Assert.That(text).Contains("@youtube");
        await Assert.That(text).Contains("AyuGram.exe");
    }

    [Test]
    public async Task IsFrozen_OnlyWhenDataStopsShort()
    {
        await Assert.That(FailoverManager.IsFrozen(18_000, 65_536)).IsTrue();
        await Assert.That(FailoverManager.IsFrozen(0, 65_536)).IsFalse();
        await Assert.That(FailoverManager.IsFrozen(65_536, 65_536)).IsFalse();
    }

    [Test]
    public async Task StrategyPicker_PausesAndCoolsDownAfterAFreeze()
    {
        var ok = new VpnStrategyResult(null, true, true, 300);
        var frozen = new VpnStrategyResult(null, true, false, 0, 18_000);
        var noHandshake = new VpnStrategyResult(null, false, false, 0);
        await Assert.That(VpnStrategyManager.PauseAfter(ok)).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(VpnStrategyManager.PauseAfter(noHandshake)).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(VpnStrategyManager.PauseAfter(frozen)).IsEqualTo(TimeSpan.FromSeconds(125));
        // no more than 2 XHTTP connections, Firefox/Edge before Chrome
        var names = VpnStrategyManager.Strategies.Select(x => x.Name).ToList();
        await Assert.That(names).DoesNotContain("xmux 6");
        await Assert.That(names.IndexOf("отпечаток firefox")).IsLessThan(names.IndexOf("отпечаток chrome"));
        await Assert.That(names.IndexOf("отпечаток edge")).IsLessThan(names.IndexOf("отпечаток chrome"));
    }
}