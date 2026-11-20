using System.Text.Json.Nodes;

namespace ServiceLib.Tests.Common;

public class CustomConfigRuleServersTests
{
    private const string Main = """
        {
          "inbounds": [ { "tag": "socks", "protocol": "socks", "listen": "127.0.0.1", "port": 10808 } ],
          "outbounds": [
            { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "fi.example.com", "port": 443 } ] } },
            { "tag": "direct", "protocol": "freedom" }
          ],
          "routing": { "rules": [ { "type": "field", "domain": [ "geosite:category-ru" ], "outboundTag": "direct" } ] }
        }
        """;

    private const string Germany = """
        {
          "inbounds": [ { "tag": "socks", "protocol": "socks", "port": 10808 } ],
          "outbounds": [
            { "tag": "direct", "protocol": "freedom" },
            { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "de.example.com", "port": 443 } ] },
              "streamSettings": { "sockopt": { "dialerProxy": "relay" } } },
            { "tag": "relay", "protocol": "vless", "settings": { "vnext": [ { "address": "ru.example.com", "port": 443 } ] } }
          ]
        }
        """;

    [Test]
    public async Task Apply_AddsInboundOutboundsAndFirstRule()
    {
        var result = JsonNode.Parse(CustomConfigRuleServers.Apply(Main, [(10850, Germany)]))!;

        var inbound = result["inbounds"]!.AsArray().Single(i => i!["tag"]!.ToString() == "detour-srv10850-in")!;
        await Assert.That(inbound["port"]!.GetValue<int>()).IsEqualTo(10850);
        await Assert.That(inbound["listen"]!.ToString()).IsEqualTo("127.0.0.1");

        var tags = result["outbounds"]!.AsArray().Select(o => o!["tag"]!.ToString()).ToList();
        await Assert.That(tags).IsEquivalentTo(new[] { "proxy", "direct", "detour-srv10850-proxy", "detour-srv10850-relay" });
        var de = result["outbounds"]!.AsArray().Single(o => o!["tag"]!.ToString() == "detour-srv10850-proxy")!;
        await Assert.That(de["streamSettings"]!["sockopt"]!["dialerProxy"]!.ToString()).IsEqualTo("detour-srv10850-relay");

        var first = result["routing"]!["rules"]![0]!;
        await Assert.That(first["inboundTag"]![0]!.ToString()).IsEqualTo("detour-srv10850-in");
        await Assert.That(first["outboundTag"]!.ToString()).IsEqualTo("detour-srv10850-proxy");
        await Assert.That(result["routing"]!["rules"]!.AsArray().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Apply_NoProxyInServer_Unchanged()
    {
        const string noProxy = """{ "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] }""";
        await Assert.That(CustomConfigRuleServers.Apply(Main, [(10850, noProxy)])).IsEqualTo(Main);
        await Assert.That(CustomConfigRuleServers.Apply(Main, [(10850, "not json")])).IsEqualTo(Main);
    }

    [Test]
    public async Task ProxyOutbounds_PrefersProxyTag_StopsOnLoop()
    {
        var outbounds = JsonNode.Parse("""
            [
              { "tag": "a", "protocol": "vmess", "proxySettings": { "tag": "b" } },
              { "tag": "proxy", "protocol": "vless", "proxySettings": { "tag": "b" } },
              { "tag": "b", "protocol": "vless", "proxySettings": { "tag": "proxy" } }
            ]
            """)!.AsArray();
        var chain = CustomConfigRuleServers.ProxyOutbounds(outbounds).Select(o => o["tag"]!.ToString()).ToList();
        await Assert.That(chain).IsEquivalentTo(new[] { "proxy", "b" });
    }
}
