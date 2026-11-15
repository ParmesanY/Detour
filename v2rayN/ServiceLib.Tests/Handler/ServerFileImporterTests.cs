namespace ServiceLib.Tests.Handler;

public class ServerFileImporterTests
{
    private const string ProtonLikeWireGuard = """
        [Interface]
        # Key for test
        PrivateKey = aGVsbG8taGVsbG8taGVsbG8taGVsbG8taGVsbG8tMTI=
        Address = 10.2.0.2/32
        DNS = 10.2.0.1

        [Peer]
        # NL#1
        PublicKey = d29ybGQtd29ybGQtd29ybGQtd29ybGQtd29ybGQtMTI=
        AllowedIPs = 0.0.0.0/0
        Endpoint = 198.51.100.10:51820
        """;

    [Test]
    public async Task Detect_WireGuardConf_EvenWithTxtExtension()
    {
        await Assert.That(ServerFileImporter.Detect("proton-NL-1.conf", ProtonLikeWireGuard)).IsEqualTo(ServerFileImporter.EKind.WireGuard);
        await Assert.That(ServerFileImporter.Detect("server.txt", ProtonLikeWireGuard)).IsEqualTo(ServerFileImporter.EKind.WireGuard);
    }

    [Test]
    public async Task Detect_XrayAndSingboxJson()
    {
        const string xray = """{ "outbounds": [ { "protocol": "vless", "settings": {} }, { "protocol": "freedom" } ] }""";
        const string singbox = """{ "outbounds": [ { "type": "vless", "server": "a.example" } ] }""";
        await Assert.That(ServerFileImporter.Detect("a.json", xray)).IsEqualTo(ServerFileImporter.EKind.XrayJson);
        await Assert.That(ServerFileImporter.Detect("b.json", singbox)).IsEqualTo(ServerFileImporter.EKind.SingboxJson);
    }

    [Test]
    public async Task Detect_OpenVpnLinksAndUnknown()
    {
        await Assert.That(ServerFileImporter.Detect("nl.ovpn", "client\nremote 1.2.3.4 1194")).IsEqualTo(ServerFileImporter.EKind.OpenVpn);
        await Assert.That(ServerFileImporter.Detect("keys.txt", "vless://abc@host:443?security=reality#name"))
            .IsEqualTo(ServerFileImporter.EKind.Links);
        await Assert.That(ServerFileImporter.Detect("x.json", "{ \"inbounds\": [] }")).IsEqualTo(ServerFileImporter.EKind.Unknown);
        await Assert.That(ServerFileImporter.Detect("x.txt", "hello")).IsEqualTo(ServerFileImporter.EKind.Unknown);
    }

    [Test]
    public async Task HasAmneziaFields_DetectsJunkKeys()
    {
        await Assert.That(ServerFileImporter.HasAmneziaFields(ProtonLikeWireGuard)).IsFalse();
        await Assert.That(ServerFileImporter.HasAmneziaFields(ProtonLikeWireGuard.Replace("DNS = 10.2.0.1", "DNS = 10.2.0.1\nJc = 4\nJmin = 40"))).IsTrue();
    }

    [Test]
    public async Task WireguardFmt_ParsesProtonLikeConf()
    {
        var items = WireguardFmt.ResolveConfig(ProtonLikeWireGuard);
        await Assert.That(items).IsNotNull();
        await Assert.That(items!.Count).IsEqualTo(1);
        await Assert.That(items[0].Address).IsEqualTo("198.51.100.10");
        await Assert.That(items[0].Port).IsEqualTo(51820);
    }
}
