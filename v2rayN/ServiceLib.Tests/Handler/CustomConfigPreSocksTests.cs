namespace ServiceLib.Tests.Handler;

public class CustomConfigPreSocksTests
{
    [Test]
    public async Task XrayConfig_SocksInboundFound()
    {
        const string json = """
            {"inbounds":[{"tag":"http","port":10809,"listen":"127.0.0.1","protocol":"http"},
                         {"tag":"socks","port":10808,"listen":"127.0.0.1","protocol":"socks"}],
             "outbounds":[{"protocol":"vless"}]}
            """;

        await ConfigHandler.DetectSocksPortInConfig(json).Should().BeEqualTo(10808);
    }

    [Test]
    public async Task SingboxConfig_MixedInboundFound()
    {
        const string json = """{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":2080}]}""";

        await ConfigHandler.DetectSocksPortInConfig(json).Should().BeEqualTo(2080);
    }

    [Test]
    [Arguments("""{"outbounds":[]}""")]
    [Arguments("""{"inbounds":[{"protocol":"http","port":10809}]}""")]
    [Arguments("""{"inbounds":[{"protocol":"socks","listen":"192.168.1.5","port":1080}]}""")]
    [Arguments("not json")]
    public async Task NoUsableSocksInbound_ReturnsNull(string json)
    {
        await ConfigHandler.DetectSocksPortInConfig(json).Should().BeNull();
    }
}
