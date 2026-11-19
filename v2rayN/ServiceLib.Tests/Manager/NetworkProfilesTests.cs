namespace ServiceLib.Tests.Manager;

public class NetworkProfilesTests
{
    [Test]
    public async Task Network_IdFromWifiNameOrRouterMac()
    {
        await Assert.That(NetworkProfiles.IdFor("Дом-5G", "AA-BB-CC-DD-EE-FF", "Wi-Fi")).IsEqualTo(new NetworkProfiles.NetworkId("wifi:Дом-5G", "Дом-5G"));
        await Assert.That(NetworkProfiles.IdFor(null, "AA-BB-CC-DD-EE-FF", "Ethernet")!.Key).IsEqualTo("gw:AA-BB-CC-DD-EE-FF");
        await Assert.That(NetworkProfiles.IdFor(null, "00-00-00-00-00-00", "Ethernet")).IsNull();
        await Assert.That(NetworkProfiles.IdFor("", null, "Ethernet")).IsNull();
    }

    [Test]
    public async Task Network_SsidFromNetsh()
    {
        const string ru = """

            На этой системе есть 1 интерфейс:

                Имя                    : Беспроводная сеть
                Описание               : Intel(R) Wi-Fi 6 AX201 160MHz
                Состояние              : подключено
                SSID                   : MegaFon_Hotspot 7
                BSSID                  : 12:34:56:78:9a:bc
                Профиль                : MegaFon_Hotspot 7
            """;
        await Assert.That(NetworkProfiles.ParseSsid(ru)).IsEqualTo("MegaFon_Hotspot 7");
        // Windows 11 24H2 without location access: no SSID line at all.
        await Assert.That(NetworkProfiles.ParseSsid("Для этой команды требуется разрешение на доступ к расположению.")).IsNull();
        await Assert.That(NetworkProfiles.ParseSsid(null)).IsNull();
    }

    [Test]
    public async Task Network_ProfileRoundTripAndCompare()
    {
        var item = new ZapretItem { Strategy = "general (ALT2).bat", Desync = "--dpi-desync=fake", DesyncNote = "registry", SiteRecords = "" };
        var p = NetworkProfiles.From(item, "Дом");
        var json = JsonUtils.Serialize(new Dictionary<string, NetworkProfile> { ["wifi:Дом"] = p });
        var back = JsonUtils.Deserialize<Dictionary<string, NetworkProfile>>(json)!;
        await Assert.That(back["wifi:Дом"].Strategy).IsEqualTo("general (ALT2).bat");
        await Assert.That(NetworkProfiles.SameStrategy(back["wifi:Дом"], item)).IsTrue();
        item.SiteRecords = "records";
        await Assert.That(NetworkProfiles.SameStrategy(back["wifi:Дом"], item)).IsFalse();
    }

    [Test]
    public async Task Whitelist_OnlyRussianRelaysAreTried()
    {
        await Assert.That(ServerNameFormatter.IsRussianRelay("🇷🇺🇸🇪 Швеция через Москву")).IsTrue();
        await Assert.That(ServerNameFormatter.IsRussianRelay("🇷🇺 → 🇩🇪 Германия")).IsTrue();
        await Assert.That(ServerNameFormatter.IsRussianRelay("🇷🇺 Москва")).IsFalse();
        await Assert.That(ServerNameFormatter.IsRussianRelay("🇩🇪 Германия 1")).IsFalse();
        await Assert.That(ServerNameFormatter.IsRussianRelay("Без флага")).IsFalse();

        var all = new List<FailoverManager.Candidate>
        {
            new("cur", 50, 0, "DE"),
            new("de2", 30, 1, "DE"),
            new("relay", 90, 2, "SE", RussianRelay: true),
        };
        await Assert.That(FailoverManager.PickNextUnderWhitelist(all, "cur", [])).IsEqualTo("relay");
        await Assert.That(FailoverManager.PickNextUnderWhitelist(all, "cur", ["relay"])).IsNull();
        await Assert.That(FailoverManager.PickNext(all, "cur", [], "DE")).IsEqualTo("de2");

        await Assert.That(FailoverManager.IsWhitelistPattern(true, [false, false, false])).IsTrue();
        await Assert.That(FailoverManager.IsWhitelistPattern(true, [false, true, false])).IsFalse();
        await Assert.That(FailoverManager.IsWhitelistPattern(false, [false, false])).IsFalse();
        await Assert.That(FailoverManager.IsWhitelistPattern(true, [])).IsFalse();
    }
}
