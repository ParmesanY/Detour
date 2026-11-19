namespace ServiceLib.Tests.Manager;

public class KillSwitchTests
{
    [Test]
    public async Task AllowedPrograms_WalksEveryCoreWithoutThrowing()
    {
        // The client's own entry has no CoreExes; iterating it threw and the kill switch never engaged.
        foreach (var core in CoreInfoManager.Instance.GetCoreInfo())
        {
            _ = CoreInfoManager.Instance.GetCoreExecFile(core, out _);
        }
        var programs = KillSwitchManager.AllowedPrograms();
        await Assert.That(programs).Contains(Utils.GetExePath());
    }

    [Test]
    public async Task EngageScript_AllowsProgramsAndLan_ThenBlocksOutbound()
    {
        var script = KillSwitchManager.BuildEngageScript([@"C:\App\v2rayN.exe", @"C:\Apps\it's\xray.exe"]);
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await lines[^1].Should().BeEqualTo("Set-NetFirewallProfile -All -DefaultOutboundAction Block");
        await script.Should().Contain(@"-Program 'C:\App\v2rayN.exe'");
        await script.Should().Contain(@"-Program 'C:\Apps\it''s\xray.exe'");
        await script.Should().Contain("-RemoteAddress LocalSubnet,10.0.0.0/8");
        // Allow rules must exist before outbound gets blocked.
        await script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal).Should()
            .BeLessThanOrEqualTo(script.IndexOf("DefaultOutboundAction Block", StringComparison.Ordinal));
    }

    [Test]
    public async Task ReleaseScript_RestoresEachProfile_NumericOrNamedEnum()
    {
        const string saved = """[{"Name":"Domain","Enabled":1,"DefaultOutboundAction":0},{"Name":"Private","Enabled":1,"DefaultOutboundAction":"Allow"},{"Name":"Public","Enabled":1,"DefaultOutboundAction":4}]""";

        var script = KillSwitchManager.BuildReleaseScript(saved);

        await script.Should().Contain($"Remove-NetFirewallRule -Group '{KillSwitchManager.RuleGroup}'");
        await script.Should().Contain("Set-NetFirewallProfile -Name 'Domain' -DefaultOutboundAction NotConfigured");
        await script.Should().Contain("Set-NetFirewallProfile -Name 'Private' -DefaultOutboundAction Allow");
        await script.Should().Contain("Set-NetFirewallProfile -Name 'Public' -DefaultOutboundAction Block");
    }

    [Test]
    public async Task ReleaseScript_UnknownState_FallsBackToAllow()
    {
        var script = KillSwitchManager.BuildReleaseScript("garbage");

        await script.Should().Contain("Set-NetFirewallProfile -All -DefaultOutboundAction Allow");
    }
}
