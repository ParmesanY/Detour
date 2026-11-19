namespace ServiceLib.Tests.Manager;

public class ConnectionLearningTests
{
    private static ConnectionItem Conn(string? process, string? host, string? ip = null, string? processPath = null, string? sniff = null)
        => new() { metadata = new MetadataItem { process = process, processPath = processPath, host = host, sniffHost = sniff, destinationIP = ip } };

    [Test]
    [Arguments("cdn.discordapp.com", "discordapp.com")]
    [Arguments("gateway.discord.gg", "discord.gg")]
    [Arguments("discord.com", "discord.com")]
    [Arguments("www.bbc.co.uk", "bbc.co.uk")]
    [Arguments("a.b.example.com.", "example.com")]
    [Arguments("localhost", null)]
    public async Task ToRegistrableDomain(string host, string? expected)
    {
        await ConnectionLearningManager.ToRegistrableDomain(host).Should().BeEqualTo(expected);
    }

    [Test]
    public async Task Collect_OnlyTargetProcess_DomainsAndPublicIps()
    {
        // Windows paths and winws-only behaviour (Linux and macOS use tpws).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var connections = new List<ConnectionItem>
        {
            Conn("Discord.exe", "cdn.discordapp.com"),
            Conn("discord.exe", "gateway.discord.gg"),
            Conn("Discord.exe", "", "162.159.130.234"),
            Conn("Discord.exe", "", "192.168.1.10"),
            Conn("Discord.exe", "", "198.18.0.5"),
            Conn("Discord.exe", null, "1.2.3.4", sniff: "media.discordapp.net"),
            Conn("chrome.exe", "youtube.com"),
            Conn(null, "x.com", processPath: @"C:\Apps\Discord\Discord.exe"),
        };

        var (domains, ips) = ConnectionLearningManager.Collect(connections, "Discord.exe");

        await domains.Should().HaveCount(4);
        await domains.Should().Contain("discordapp.com");
        await domains.Should().Contain("discord.gg");
        await domains.Should().Contain("discordapp.net");
        await domains.Should().Contain("x.com");
        await domains.Should().NotContain("youtube.com");
        await ips.Should().HaveCount(1);
        await ips.Should().Contain("162.159.130.234");
    }

    [Test]
    public async Task Collect_ByFullPath()
    {
        var connections = new List<ConnectionItem>
        {
            Conn("app.exe", "a.example.com", processPath: @"C:\One\app.exe"),
            Conn("app.exe", "b.other.com", processPath: @"C:\Two\app.exe"),
        };

        var (domains, _) = ConnectionLearningManager.Collect(connections, @"C:\One\app.exe");

        await domains.Should().HaveCount(1);
        await domains.Should().Contain("example.com");
    }

    [Test]
    public async Task LearnedDomains_FeedZapretAndRouting()
    {
        var rule = new ConnectionRule
        {
            Target = "Discord.exe",
            Mode = EConnectionMode.Zapret,
            LearnedDomains = ["discord.com", "discordapp.net"],
            LearnedIps = ["162.159.130.234"],
        };

        var c = ConnectionRuleCompiler.Compile([rule]);

        await c.Warnings.Should().BeEmpty();
        await c.ZapretDomains.Should().Contain("discordapp.net");
        await c.ZapretIps.Should().Contain("162.159.130.234");
        // process, QUIC block for the domains, domains, IPs
        await c.RoutingRules.Should().HaveCount(4);
    }

    [Test]
    public async Task ReplaceFromText_KeepsLearnedTargetsAcrossModeChange()
    {
        var config = new Config
        {
            ConnectionRules = [new() { Target = "Discord.exe", Mode = EConnectionMode.Zapret, LearnedDomains = ["discord.com"] }],
        };

        ConnectionRulesManager.ReplaceFromText(config, "Discord.exe +zapret+vpn");

        await config.ConnectionRules[0].Mode.Should().BeEqualTo(EConnectionMode.ZapretThenVpn);
        await config.ConnectionRules[0].LearnedDomains!.Should().Contain("discord.com");
    }
}
