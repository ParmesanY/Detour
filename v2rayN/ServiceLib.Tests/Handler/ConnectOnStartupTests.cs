namespace ServiceLib.Tests.Handler;

public class ConnectOnStartupTests
{
    private static Config Make(bool? connect, bool? preferTun, string? indexId = "abc", bool tun = false,
        ESysProxyType sys = ESysProxyType.ForcedClear) => new()
    {
        IndexId = indexId,
        UiItem = new() { ConnectOnStartup = connect, PreferTunConnect = preferTun },
        TunModeItem = new() { EnableTun = tun },
        SystemProxyItem = new() { SysProxyType = sys },
    };

    [Test]
    public async Task On_ConnectsViaTun_EvenIfLastSessionEndedDisconnected()
    {
        var c = Make(true, true);
        ConfigHandler.ApplyConnectOnStartup(c);
        await Assert.That(c.TunModeItem.EnableTun).IsTrue();
        await Assert.That(c.SystemProxyItem.SysProxyType).IsEqualTo(ESysProxyType.ForcedClear);
    }

    [Test]
    public async Task On_ProxyMode_SetsSystemProxy()
    {
        var c = Make(null, false, tun: true);
        ConfigHandler.ApplyConnectOnStartup(c);
        await Assert.That(c.TunModeItem.EnableTun).IsFalse();
        await Assert.That(c.SystemProxyItem.SysProxyType).IsEqualTo(ESysProxyType.ForcedChange);
    }

    [Test]
    public async Task Off_StartsDisconnected()
    {
        var c = Make(false, true, tun: true, sys: ESysProxyType.ForcedChange);
        ConfigHandler.ApplyConnectOnStartup(c);
        await Assert.That(c.TunModeItem.EnableTun).IsFalse();
        await Assert.That(c.SystemProxyItem.SysProxyType).IsEqualTo(ESysProxyType.ForcedClear);
    }

    [Test]
    public async Task NoServer_StaysDisconnected()
    {
        var c = Make(true, true, indexId: null);
        ConfigHandler.ApplyConnectOnStartup(c);
        await Assert.That(c.TunModeItem.EnableTun).IsFalse();
    }
}
