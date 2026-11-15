namespace ServiceLib.Tests.Common;

public class CustomConfigDomainsTests
{
    [Test]
    public async Task Extract_XrayVnextServersAndFlatAddress()
    {
        const string json = """
            {
              // comments are allowed in subscription configs
              "outbounds": [
                { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "fin.example-vpn.net", "port": 443 } ] } },
                { "tag": "YT_A", "protocol": "vless", "settings": { "address": "vid-a.example-cdn.org", "port": 443 } },
                { "tag": "ss", "protocol": "shadowsocks", "settings": { "servers": [ { "address": "1.2.3.4", "port": 8388 } ] } },
                { "tag": "hy", "protocol": "hysteria", "settings": { "servers": [ { "address": "hy.example-vpn.net" } ] } },
                { "tag": "x", "protocol": "vless", "settings": { "vnext": [ { "address": "main.example.net" } ] },
                  "streamSettings": { "xhttpSettings": { "extra": { "downloadSettings": { "address": "down.example.net" } } } } },
                { "tag": "wg", "protocol": "wireguard", "settings": { "peers": [ { "endpoint": "wg.example.net:51820" } ] } },
                { "tag": "direct", "protocol": "freedom" },
              ]
            }
            """;

        var domains = CustomConfigDomains.Extract(json);

        await Assert.That(domains).Contains("fin.example-vpn.net");
        await Assert.That(domains).Contains("vid-a.example-cdn.org");
        await Assert.That(domains).Contains("hy.example-vpn.net");
        await Assert.That(domains).Contains("main.example.net");
        await Assert.That(domains).Contains("down.example.net");
        await Assert.That(domains).Contains("wg.example.net");
        await Assert.That(domains).DoesNotContain("1.2.3.4");
        await Assert.That(domains.Count).IsEqualTo(6);
    }

    [Test]
    public async Task Extract_SingboxServerField()
    {
        const string json = """{ "outbounds": [ { "type": "vless", "server": "sb.example.net", "server_port": 443 }, { "type": "direct" } ] }""";

        await Assert.That(CustomConfigDomains.Extract(json).Single()).IsEqualTo("sb.example.net");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("""{ "inbounds": [] }""")]
    public async Task Extract_BadInput_Empty(string? json)
    {
        await Assert.That(CustomConfigDomains.Extract(json).Count).IsEqualTo(0);
    }
}
