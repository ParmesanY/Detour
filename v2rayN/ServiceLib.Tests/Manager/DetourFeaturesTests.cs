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

        var wg = WireguardFmt.ResolveConfig(WarpManager.BuildWireGuardConf(reg));
        await Assert.That(wg!.Count).IsEqualTo(1);
        await Assert.That(wg[0].Port).IsEqualTo(2408);

        var xray = JsonNode.Parse(WarpManager.BuildXrayNoiseConfig(reg, 10808))!;
        await Assert.That(xray["inbounds"]![0]!["port"]!.GetValue<int>()).IsEqualTo(10808);
        await Assert.That(xray["outbounds"]![0]!["streamSettings"]!["finalmask"]!["udp"]![0]!["type"]!.GetValue<string>()).IsEqualTo("noise");
        await Assert.That(ServerFileImporter.Detect("warp.json", xray.ToJsonString())).IsEqualTo(ServerFileImporter.EKind.XrayJson);
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
