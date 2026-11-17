namespace ServiceLib.Tests.Manager;

public class StrategyRegistryTests
{
    private static readonly HashSet<string> _google = ["youtube.com", "googlevideo.com"];

    private static readonly string[] _urls = ["https://rutracker.org/", "https://www.youtube.com/", "https://x.com/"];

    [Test]
    public async Task Urls_on_the_google_list_are_not_the_general_profiles()
    {
        await StrategyRegistry.GeneralIndexes(_urls, _google).Should().BeEquivalentTo([0, 2]);
    }

    [Test]
    public async Task Target_only_when_all_urls_are_one_public_target()
    {
        await StrategyRegistry.TargetFor(["https://rutracker.org/", "https://rutracker.org/forum/"]).Should().BeEqualTo("rutracker");
        await StrategyRegistry.TargetFor(["https://rutracker.org/", "https://x.com/"]).Should().BeNull();
        await StrategyRegistry.TargetFor(["https://my-secret-site.example/"]).Should().BeNull();
    }

    private static Config NewConfig() => new() { BlockMapItem = new BlockMapItem { Enabled = true, Net = "home" } };

    [Test]
    public async Task Report_counts_only_general_urls_and_needs_a_blocked_baseline()
    {
        var results = new Dictionary<string, bool[]>
        {
            ["registry:abc"] = [true, false, true],
            ["general.bat"] = [false, true, false],
            ["broken.bat"] = [true, true, true],
        };
        var desync = new Dictionary<string, string>
        {
            ["registry:abc"] = "--dpi-desync=fake --dpi-desync-fooling=ts",
            ["general.bat"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1",
            ["broken.bat"] = "--debug=@C:\\x.txt",
        };
        var report = StrategyRegistry.BuildReport(NewConfig(), _urls, [false, true, false], results, desync, _google)!;
        await report.Should().NotBeNull();
        await report.Baseline.Passed.Should().BeEqualTo(0);
        await report.Baseline.Total.Should().BeEqualTo(2);
        await report.Results.Should().HaveCount(2);
        await report.Results.Single(r => r.Args.Contains("fooling")).Passed.Should().BeEqualTo(2);
        await report.Results.Single(r => r.Args.Contains("multisplit")).Passed.Should().BeEqualTo(0);
        await report.Env.DpiBypass.Should().BeFalse();
        await report.Target.Should().BeNull();
    }

    [Test]
    public async Task Nothing_to_report_when_the_general_urls_open_without_a_bypass()
    {
        var results = new Dictionary<string, bool[]> { ["general.bat"] = [true, true, true] };
        var desync = new Dictionary<string, string> { ["general.bat"] = "--dpi-desync=multisplit" };
        await StrategyRegistry.BuildReport(NewConfig(), _urls, [true, false, true], results, desync, _google).Should().BeNull();
    }

    [Test]
    public async Task Display_name_marks_a_registry_strategy()
    {
        await ZapretManager.DisplayStrategy(new ZapretItem { Strategy = "general.bat" }).Should().BeEqualTo("general.bat");
        await ZapretManager.DisplayStrategy(new ZapretItem { Strategy = "general.bat", Desync = "--dpi-desync=fake" }).Should().Contain("general.bat");
        await ZapretManager.DisplayStrategy(new ZapretItem { Strategy = "general.bat", Desync = "--dpi-desync=fake" }).Should().NotBeEqualTo("general.bat");
    }
}
