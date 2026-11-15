namespace ServiceLib.Tests.Manager;

public class ZapretStrategySelectionTests
{
    private static Task<(ZapretManager.StrategyScore? Best, List<ZapretManager.StrategyScore> All)> Run(
        Dictionary<string, int> passedByStrategy, bool stopAtFirst, HashSet<string>? failingToStart = null)
    {
        var current = "";
        return ZapretManager.SelectStrategyAsync(
            [.. passedByStrategy.Keys],
            s => { current = s; return Task.FromResult(failingToStart?.Contains(s) != true); },
            _ => Task.FromResult(passedByStrategy[current]),
            total: 2,
            stopAtFirst,
            progress: null,
            CancellationToken.None);
    }

    [Test]
    public async Task PicksStrategyWithMostPasses()
    {
        var (best, all) = await Run(new() { ["general.bat"] = 0, ["general (ALT).bat"] = 1, ["general (ALT2).bat"] = 2 }, stopAtFirst: false);

        await best!.Strategy.Should().BeEqualTo("general (ALT2).bat");
        await all.Should().HaveCount(3);
    }

    [Test]
    public async Task StopsAtFirstFullPass()
    {
        var (best, all) = await Run(new() { ["general.bat"] = 1, ["general (ALT).bat"] = 2, ["general (ALT2).bat"] = 2 }, stopAtFirst: true);

        await best!.Strategy.Should().BeEqualTo("general (ALT).bat");
        await all.Should().HaveCount(2);
    }

    [Test]
    public async Task StrategyThatFailsToStart_ScoresZero()
    {
        var (best, all) = await Run(new() { ["general.bat"] = 2, ["general (ALT).bat"] = 1 }, stopAtFirst: false,
            failingToStart: ["general.bat"]);

        await best!.Strategy.Should().BeEqualTo("general (ALT).bat");
        await all[0].Passed.Should().BeEqualTo(0);
    }

    [Test]
    public async Task NothingWorks_BestHasZeroPasses()
    {
        var (best, _) = await Run(new() { ["general.bat"] = 0, ["general (ALT).bat"] = 0 }, stopAtFirst: true);

        await best!.Passed.Should().BeEqualTo(0);
    }

    [Test]
    public async Task CheckUrls_UseZapretRulesOnly()
    {
        var config = new Config
        {
            ConnectionRules =
            [
                new() { Target = "@discord", Mode = EConnectionMode.ZapretThenVpn },
                new() { Target = "@youtube", Mode = EConnectionMode.ZapretThenVpn, FallbackActive = true },
                new() { Target = "rutracker.org", Mode = EConnectionMode.Zapret },
                new() { Target = "chatgpt.com", Mode = EConnectionMode.Vpn },
            ],
        };

        var urls = ZapretManager.GetCheckUrls(config);

        // zapret and zapret→VPN rules (even on VPN fallback: the strategy is picked to get them off it).
        await urls.Should().Contain("https://discord.com/");
        await urls.Should().Contain("https://gateway.discord.gg/");
        await urls.Should().Contain("https://rutracker.org/");
        await urls.Should().Contain("https://www.youtube.com/");
        await urls.Should().NotContain("https://chatgpt.com/");
    }

    [Test]
    [Arguments("https://github.com/Flowseal/zapret-discord-youtube/releases/tag/1.10.4", "1.10.4")]
    [Arguments("https://github.com/x/y/releases/tag/v2.0/", "v2.0")]
    [Arguments("https://github.com/x/y/releases/tag/1.0?foo=1", "1.0")]
    [Arguments("https://github.com/x/y/releases", null)]
    [Arguments(null, null)]
    public async Task ParseReleaseTag(string? url, string? expected)
    {
        await ZapretManager.ParseReleaseTag(url).Should().BeEqualTo(expected);
    }

    [Test]
    public async Task CheckUrls_DefaultWhenNoZapretRules()
    {
        var urls = ZapretManager.GetCheckUrls(new Config { ConnectionRules = [] });

        await urls.Should().Contain("https://discord.com/");
    }
}
