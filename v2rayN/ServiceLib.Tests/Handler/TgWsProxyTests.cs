using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace ServiceLib.Tests.Handler;

public class TgWsProxyTests
{
    [Test]
    public async Task NewSecret_IsValid32Hex()
    {
        var s = TgWsProxyManager.NewSecret();
        await Assert.That(s.Length).IsEqualTo(32);
        await Assert.That(TgWsProxyManager.IsValidSecret(s)).IsTrue();
        await Assert.That(TgWsProxyManager.NewSecret()).IsNotEqualTo(s);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("3075abe65830f0325116bb0416cadf9")]
    [Arguments("3075abe65830f0325116bb0416cadf9fa")]
    [Arguments("zz75abe65830f0325116bb0416cadf9f")]
    public async Task IsValidSecret_RejectsBad(string? secret)
    {
        await Assert.That(TgWsProxyManager.IsValidSecret(secret)).IsFalse();
    }

    [Test]
    public async Task StandaloneSecret_SamePortOnly()
    {
        const string cfg = """{"port": 1443, "host": "127.0.0.1", "secret": "7C0137A77C0137A77C0137A77C0137A7"}""";

        // Same port: clients added to that copy reach ours, so ours must answer with its secret.
        await Assert.That(TgWsProxyManager.ParseStandaloneSecret(cfg, 1443)).IsEqualTo("7c0137a77c0137a77c0137a77c0137a7");
        await Assert.That(TgWsProxyManager.ParseStandaloneSecret(cfg, 1444)).IsNull();
        // No port in its config: the tray app's default.
        await Assert.That(TgWsProxyManager.ParseStandaloneSecret("""{"secret": "7c0137a77c0137a77c0137a77c0137a7"}""", TgWsProxyManager.DefaultPort))
            .IsEqualTo("7c0137a77c0137a77c0137a77c0137a7");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"port": 1443}""")]
    [Arguments("""{"port": 1443, "secret": "short"}""")]
    [Arguments("""{"port": "1443", "secret": 5}""")]
    public async Task StandaloneSecret_BadConfig_IsNull(string? json)
    {
        await Assert.That(TgWsProxyManager.ParseStandaloneSecret(json, 1443)).IsNull();
    }

    [Test]
    public async Task BuildLink_UsesDdPrefix()
    {
        var link = TgWsProxyManager.BuildLink(1443, "3075abe65830f0325116bb0416cadf9f");
        await Assert.That(link).IsEqualTo("tg://proxy?server=127.0.0.1&port=1443&secret=dd3075abe65830f0325116bb0416cadf9f");
    }

    [Test]
    public async Task AssetName_ByArchitecture()
    {
        await Assert.That(TgWsProxyManager.AssetName(Architecture.X64)).IsEqualTo("TgWsProxy_windows.exe");
        await Assert.That(TgWsProxyManager.AssetName(Architecture.Arm64)).IsEqualTo("TgWsProxy_windows_arm64.exe");
    }

    [Test]
    public async Task BuildConfigJson_KeepsOtherKeys_OverridesOurs()
    {
        const string existing = """{"host":"0.0.0.0","port":9999,"secret":"x","pool_size":8,"check_updates":true,"autostart":true}""";
        var cfg = JsonNode.Parse(TgWsProxyManager.BuildConfigJson(existing, 1555, "3075abe65830f0325116bb0416cadf9f"))!.AsObject();
        await Assert.That(cfg["host"]!.GetValue<string>()).IsEqualTo("127.0.0.1");
        await Assert.That(cfg["port"]!.GetValue<int>()).IsEqualTo(1555);
        await Assert.That(cfg["secret"]!.GetValue<string>()).IsEqualTo("3075abe65830f0325116bb0416cadf9f");
        await Assert.That(cfg["pool_size"]!.GetValue<int>()).IsEqualTo(8);
        await Assert.That(cfg["check_updates"]!.GetValue<bool>()).IsFalse();
        await Assert.That(cfg["autostart"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("[1,2]")]
    public async Task BuildConfigJson_BadExisting_StartsFresh(string? existing)
    {
        var cfg = JsonNode.Parse(TgWsProxyManager.BuildConfigJson(existing, 1443, "3075abe65830f0325116bb0416cadf9f"))!.AsObject();
        await Assert.That(cfg["port"]!.GetValue<int>()).IsEqualTo(1443);
    }

    [Test]
    public async Task DirectRule_SendsProxyProcessDirect()
    {
        var rule = TgWsProxyManager.DirectRule();
        await Assert.That(rule.OutboundTag).IsEqualTo(Global.DirectTag);
        await Assert.That(rule.Process!.Single()).IsEqualTo(TgWsProxyManager.ExeName);
        await Assert.That(rule.ProcessAndIp == true).IsTrue();
        await Assert.That(rule.Ip!).Contains("149.154.160.0/20");
        await Assert.That(rule.Remarks!.StartsWith(ConnectionRuleCompiler.RemarksPrefix)).IsTrue();
    }
}
