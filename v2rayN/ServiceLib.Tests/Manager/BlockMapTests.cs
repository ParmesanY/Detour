using Detour.NetProbe;

namespace ServiceLib.Tests.Manager;

public class BlockMapTests
{
    private static readonly DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task IsDue_OnlyWhenOptedIn()
    {
        await BlockMapManager.IsDue(new BlockMapItem { Enabled = false }, _now, false).Should().BeFalse();
        await BlockMapManager.IsDue(new BlockMapItem { Enabled = true }, _now, false).Should().BeTrue();
    }

    [Test]
    public async Task IsDue_NeverWhileBypassRuns()
    {
        await BlockMapManager.IsDue(new BlockMapItem { Enabled = true }, _now, bypassRunning: true).Should().BeFalse();
    }

    [Test]
    public async Task IsDue_CountsIntervalFromLastAttempt()
    {
        var recent = new BlockMapItem { Enabled = true, LastAttempt = _now - TimeSpan.FromHours(2) };
        var old = new BlockMapItem { Enabled = true, LastAttempt = _now - BlockMapManager.Interval };
        await BlockMapManager.IsDue(recent, _now, false).Should().BeFalse();
        await BlockMapManager.IsDue(old, _now, false).Should().BeTrue();
    }

    [Test]
    public async Task Client_IsDetourWithVersion()
    {
        await BlockMapManager.Client.Should().StartWith("detour/");
    }

    [Test]
    public async Task Describe_ShowsVerdictsAndTheJsonThatIsSent_WithoutAddresses()
    {
        var report = new ProbeReport
        {
            Client = "detour/7.25.6",
            TargetsVer = TargetList.Default.Version,
            Net = "home",
            Ts = "2026-11-16T12:00Z",
            Env = new ProbeEnv { VpnAdapter = true, Nic = true },
            Results =
            [
                new TargetResult { T = "x", Dns = "ok", Tcp = "ok", Tls = "timeout", TlsCtrl = "ok" },
                new TargetResult { T = "telegram-dc2", Tcp = "timeout" },
            ],
        };

        var text = BlockMapManager.Describe(new BlockMapRun(report, null, "measured"));

        await text.Should().Contain("X (Twitter)");
        await text.Should().Contain("sni");
        await text.Should().Contain("\"client\": \"detour/7.25.6\"");
        await text.Should().Contain("\"tls_ctrl\": \"ok\"");
        await text.Should().NotContain("192.168.");
    }

    [Test]
    public async Task Describe_PrefersTheCollectorsVerdicts()
    {
        var report = new ProbeReport
        {
            Client = "detour/7.25.6",
            Net = "home",
            Ts = "2026-11-16T12:00Z",
            Env = new ProbeEnv { Nic = true },
            Results = [new TargetResult { T = "x", Dns = "ok", Tcp = "ok", Tls = "timeout", TlsCtrl = "ok" }],
        };
        var sent = new SendResult { Accepted = true, Verdicts = new() { ["x"] = ["from-server"] } };

        var text = BlockMapManager.Describe(new BlockMapRun(report, sent, "sent"));

        await text.Should().Contain("from-server");
    }

    [Test]
    public async Task NewItem_IsOptOut()
    {
        var item = new BlockMapItem();
        await item.Enabled.Should().BeFalse();
        await item.Net.Should().BeEqualTo("home");
    }
}
