namespace ServiceLib.Tests.Manager;

public class EmergencyModeTests
{
    [Test]
    public async Task ParseBridges_KeepsOnlyBridgeLines_NoTorrcInjection()
    {
        var text = """
            Bridge webtunnel 192.0.2.10:443 0123456789ABCDEF0123456789ABCDEF01234567 url=https://example.org/path ver=0.0.1

            obfs4 198.51.100.2:443 89ABCDEF0123456789ABCDEF0123456789ABCDEF cert=abc iat-mode=0
            SocksPort 0.0.0.0:9050
            webtunnel 192.0.2.10:443 0123456789ABCDEF0123456789ABCDEF01234567 url=https://example.org/path ver=0.0.1
            meek_lite 192.0.2.20:80 url=https://x front=y
            """;
        var lines = TorManager.ParseBridges(text);
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).StartsWith("webtunnel ");
        await Assert.That(lines[1]).StartsWith("obfs4 ");
        await Assert.That(lines.Any(l => l.Contains("SocksPort"))).IsFalse();
    }

    [Test]
    public async Task Torrc_LoopbackOnly_ClientOnly_Bridges()
    {
        var torrc = TorManager.BuildTorrc(10868, ["webtunnel 192.0.2.10:443 X url=https://e.org"], @"C:\Detour\bin\tor\lyrebird.exe", @"C:\Detour\guiConfigs\tor\data");
        await Assert.That(torrc).Contains("SocksPort 127.0.0.1:10868");
        await Assert.That(torrc).Contains("ClientOnly 1");
        await Assert.That(torrc).Contains("UseBridges 1");
        await Assert.That(torrc).Contains("Bridge webtunnel 192.0.2.10:443");
        await Assert.That(torrc).Contains("ClientTransportPlugin snowflake,webtunnel,obfs4 exec lyrebird.exe");
        await Assert.That(torrc).DoesNotContain("ORPort");
    }

    [Test]
    public async Task Progress_FromTorLog()
    {
        await Assert.That(TorManager.ParseProgress("Nov 21 20:00:01.000 [notice] Bootstrapped 45% (requesting_descriptors): Asking for relay descriptors")).IsEqualTo(45);
        await Assert.That(TorManager.ParseProgress("[notice] Bootstrapped 100% (done): Done")).IsEqualTo(100);
        await Assert.That(TorManager.ParseProgress("[notice] Opening Socks listener")).IsNull();
    }

    [Test]
    public async Task BuiltinSnowflake_ValidAndHasAmpCacheFallback()
    {
        var lines = TorManager.ParseBridges(string.Join("\n", TorManager.BuiltinSnowflake));
        await Assert.That(lines.Count).IsEqualTo(TorManager.BuiltinSnowflake.Count);
        await Assert.That(TorManager.BuiltinSnowflake.Any(l => l.Contains("ampcache=https://cdn.ampproject.org/"))).IsTrue();
    }

    [Test]
    public async Task Rules_TorDirect_TorrentsAndUpdatesBlocked()
    {
        var rules = EmergencyModeManager.Rules();
        await Assert.That(rules[0].OutboundTag).IsEqualTo(Global.DirectTag);
        await Assert.That(rules[0].Process!).Contains("tor.exe");
        await Assert.That(rules[0].Process!).Contains("lyrebird.exe");
        await Assert.That(rules.Any(r => r.OutboundTag == Global.BlockTag && r.Protocol?.Contains("bittorrent") == true)).IsTrue();
        await Assert.That(rules.Any(r => r.OutboundTag == Global.BlockTag && r.Domain?.Contains("domain:windowsupdate.com") == true)).IsTrue();
    }
}
