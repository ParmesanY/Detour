namespace ServiceLib.Tests.Common;

public class ServerNameFormatterTests
{
    [Test]
    [Arguments("🇫🇮 [VL] Финляндия", "FI", "[VL] Финляндия")]
    [Arguments("🇪🇺 Премиум АВТО-ВЫБОР", "EU", "Премиум АВТО-ВЫБОР")]
    [Arguments("Германия 🇩🇪 fast", "DE", "Германия fast")]
    [Arguments("Без флага", null, "Без флага")]
    [Arguments("🚀 Turbo", null, "🚀 Turbo")]
    [Arguments("", null, "")]
    public async Task Split(string remarks, string? code, string name)
    {
        var (c, n) = ServerNameFormatter.Split(remarks);

        await c.Should().BeEqualTo(code);
        await n.Should().BeEqualTo(name);
    }
}
