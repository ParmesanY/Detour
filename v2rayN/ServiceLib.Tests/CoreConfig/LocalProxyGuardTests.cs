namespace ServiceLib.Tests.CoreConfig;

public class LocalProxyGuardTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Xray_LocalPortHasPasswordOnlyInTun(bool tun)
    {
        var config = tun ? CoreConfigTestFactory.CreateConfigWithTun(ECoreType.Xray, true) : CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var result = new CoreConfigV2rayService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because(result.Msg);
        var socks = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!.inbounds
            .First(i => i.tag == nameof(EInboundProtocol.socks));
        await (socks.settings?.auth == "password").Should().BeEqualTo(tun);
        if (tun)
        {
            await socks.settings!.accounts![0].user.Should().BeEqualTo(LocalProxyGuard.SessionCredentials.User);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Singbox_LocalPortHasPasswordOnlyInTun(bool tun)
    {
        var config = tun ? CoreConfigTestFactory.CreateConfigWithTun(ECoreType.sing_box, true) : CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        var result = new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box)).GenerateClientConfigContent();

        await result.Success.Should().BeTrue().Because(result.Msg);
        var mixed = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!.inbounds
            .First(i => i.tag == nameof(EInboundProtocol.socks));
        await (mixed.users?.Count > 0).Should().BeEqualTo(tun);
    }

    [Test]
    public async Task CanBeSwitchedOff()
    {
        var config = new Config { ProtectLocalProxy = false };

        await LocalProxyGuard.ShouldProtect(config, true).Should().BeFalse();
        await LocalProxyGuard.ShouldProtect(new Config(), true).Should().BeTrue();
        await LocalProxyGuard.ShouldProtect(new Config(), false).Should().BeFalse();
    }
}
