using System.Text.Json.Nodes;
using Detour.NetProbe;

namespace ServiceLib.Tests.Manager;

public class TransportAndAwgTests
{
    [Test]
    public async Task Kind_OfProfiles()
    {
        await Assert.That(TransportStats.Kind(EConfigType.VLESS, "tcp", "reality")).IsEqualTo("vless-reality");
        await Assert.That(TransportStats.Kind(EConfigType.VLESS, "xhttp", "reality")).IsEqualTo("vless-reality-xhttp");
        await Assert.That(TransportStats.Kind(EConfigType.VLESS, "xhttp", "tls")).IsEqualTo("vless-xhttp");
        await Assert.That(TransportStats.Kind(EConfigType.VLESS, "ws", "tls")).IsEqualTo("vless-ws");
        await Assert.That(TransportStats.Kind(EConfigType.VLESS, "tcp", "tls")).IsEqualTo("vless-tls");
        await Assert.That(TransportStats.Kind(EConfigType.Hysteria2, null, null)).IsEqualTo("hysteria2");
        await Assert.That(TransportStats.Kind(EConfigType.WireGuard, null, null, amnezia: true)).IsEqualTo("amneziawg");
        await Assert.That(TransportStats.Kind(EConfigType.SOCKS, null, null)).IsEqualTo("other");
    }

    [Test]
    public async Task Kinds_AreAllKnownToTheMap()
    {
        foreach (var type in Enum.GetValues<EConfigType>())
        {
            foreach (var (net, sec) in new[] { ("tcp", "reality"), ("xhttp", "reality"), ("xhttp", ""), ("ws", "tls"), ("grpc", "tls"), ("tcp", "tls"), ("", "") })
            {
                await Assert.That(TransportReport.Kinds).Contains(TransportStats.Kind(type, net, sec));
            }
        }
    }

    [Test]
    public async Task PickNext_PoorTransportOnThisIspGoesLater()
    {
        var candidates = new List<FailoverManager.Candidate>
        {
            new("cur", 100, 0, "DE"),
            new("reality", 50, 1, "DE", TransportScore: 0.2),
            new("hy2", 300, 2, "DE", TransportScore: 0.9),
            new("unknown", 80, 3, "DE"),
        };
        await Assert.That(FailoverManager.PickNext(candidates, "cur", [], "DE")).IsEqualTo("hy2");
        await Assert.That(FailoverManager.PickNext(candidates, "cur", ["hy2"], "DE")).IsEqualTo("unknown");
        await Assert.That(FailoverManager.PickNext(candidates, "cur", ["hy2", "unknown"], "DE")).IsEqualTo("reality");
    }

    [Test]
    public async Task Awg3_KeysReachTheCore_Awg15KeysDont()
    {
        var conf = new Dictionary<string, string>
        {
            ["Jc"] = "4", ["H1"] = "100-200", ["I1"] = "<b 0xc700>",
            ["headerprotectionkey"] = "QGg8AFRn6qKfTB7cT3FWH1WGx3np+OKzlNuQUrqIBmI=",
            ["RekeyAfterTime"] = "100-140", ["KeepaliveTimeout"] = "10", ["RandomTrailers"] = "1", ["DisableCookies"] = "0",
            ["J1"] = "<r 2>", ["Itime"] = "60",
        };
        var json = JsonNode.Parse(AmneziaCore.FromConf(conf)!)!.AsObject();
        await Assert.That(json["jc"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(json["h1"]!.GetValue<string>()).IsEqualTo("100-200");
        await Assert.That(json["header_protection_key"]!.GetValue<string>()).StartsWith("QGg8");
        await Assert.That(json["rekey_after_time"]!.GetValue<string>()).IsEqualTo("100-140");
        await Assert.That(json["keepalive_timeout"]!.GetValue<long>()).IsEqualTo(10);
        await Assert.That(json["random_trailers"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json.ContainsKey("disable_cookies")).IsFalse();
        await Assert.That(json.ContainsKey("j1")).IsFalse();
        await Assert.That(json.ContainsKey("itime")).IsFalse();

        // a profile stored by an older build
        var old = AmneziaCore.ToJson("""{"jc":4,"j1":"<r 2>","itime":60}""")!;
        await Assert.That(old.Select(kv => kv.Key)).IsEquivalentTo(new[] { "jc" });
    }
}
