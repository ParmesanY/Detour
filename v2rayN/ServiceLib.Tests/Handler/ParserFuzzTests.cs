using System.Diagnostics;

namespace ServiceLib.Tests.Handler;

/// <summary>
/// Everything here parses data from the network or from strangers (subscriptions, share links, provider links,
/// published lists, Flowseal .bat files). A malformed or hostile input must come back as "not valid", never as
/// an unhandled exception (which takes the whole update down) or a hang. Mutation fuzzing with a fixed seed:
/// a failure prints the input, and the same run repeats it.
/// </summary>
public class ParserFuzzTests
{
    private const int Rounds = 400;
    private static readonly TimeSpan _limit = TimeSpan.FromSeconds(3);

    private static readonly string[] _links =
    [
        "vless://0b3a5c2e-1111-2222-3333-444455556666@example.com:443?encryption=none&security=reality&sni=www.google.com&fp=chrome&pbk=abc&sid=12&type=tcp&flow=xtls-rprx-vision#Server%20NL",
        "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"v":"2","ps":"de","add":"1.2.3.4","port":"443","id":"0b3a5c2e-1111-2222-3333-444455556666","aid":"0","net":"ws","type":"none","host":"a.com","path":"/ws","tls":"tls"}""")),
        "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:password")) + "@5.6.7.8:8388#ss",
        "trojan://pass@example.org:443?security=tls&sni=example.org&type=grpc&serviceName=x#trojan",
        "hysteria2://auth@example.net:8443?sni=example.net&obfs=salamander&obfs-password=x#hy2",
        "tuic://0b3a5c2e-1111-2222-3333-444455556666:pw@example.com:443?congestion_control=bbr&alpn=h3#tuic",
        "wireguard://cHJpdmF0ZQ==@9.9.9.9:51820?publickey=cHVibGlj&address=10.0.0.2/32&mtu=1280#wg",
        "socks://dXNlcjpwYXNz@127.0.0.1:1080#socks",
        "anytls://pw@example.com:443?sni=example.com#anytls",
    ];

    private static readonly string[] _texts =
    [
        "happ://add/https://sub.example.com/abc?token=1",
        "incy://import/https%3A%2F%2Fsub.example.com%2Fx",
        "v2raytun://import/https://sub.example.com/y",
        "upload=123; download=456; total=1073741824; expire=1893456000",
        "youtube.com -> zapret",
        "app:telegram.exe -> vpn",
        "ip:10.0.0.0/8 -> direct",
        "preset:ai -> zapret→vpn",
        """[{"name":"OVH","asns":[16276]},{"name":"HostPapa","patterns":["hostpapa"]}]""",
        """[{"name":"split 2","packets":"tlshello","lengths":"1-3","delays":"0"}]""",
        "\"16276 | 51.68.0.0/16 | FR | ripencc | 2010-01-01\"",
        """{"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443}]}}]}""",
    ];

    private const string Bat = """
        @echo off
        set "BIN=%~dp0bin\"
        set "LISTS=%~dp0lists\"
        start "zapret" /min "%BIN%winws.exe" --wf-tcp=80,443 --wf-udp=443 ^
        --filter-udp=443 --hostlist="%LISTS%list-general.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
        --filter-tcp=443 --hostlist="%LISTS%list-general.txt" --dpi-desync=fake,multisplit --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-fake-tls=^! --dpi-desync-repeats=6
        """;

    private static string Mutate(Random rnd, string seed)
    {
        var s = new StringBuilder(seed);
        var specials = new[] { "\0", "\u202E", "%", "%%", "%00", "^", "\"", "'", "’", "\\", "../", "..\\", "@", ":", "#", "?", "&", "=", "{", "}", "[", "]", "\n", "\r\n", "😀", "\uFFFF", "-1", "99999999999", "NaN", "null" };
        var steps = rnd.Next(1, 6);
        for (var i = 0; i < steps && s.Length > 0; i++)
        {
            var pos = rnd.Next(s.Length);
            switch (rnd.Next(7))
            {
                case 0: s.Remove(pos, Math.Min(rnd.Next(1, 20), s.Length - pos)); break;
                case 1: s.Insert(pos, specials[rnd.Next(specials.Length)]); break;
                case 2: s[pos] = (char)rnd.Next(1, 0x2FF); break;
                case 3: s.Length = pos; break;
                case 4: s.Insert(pos, s.ToString(pos, Math.Min(50, s.Length - pos)), rnd.Next(1, 200)); break;
                case 5: s.Insert(pos, new string('A', rnd.Next(1, 5000))); break;
                default:
                    // Base64 bodies: mutate inside the encoding too.
                    s.Insert(pos, Convert.ToBase64String(Encoding.UTF8.GetBytes(specials[rnd.Next(specials.Length)])));
                    break;
            }
        }
        return s.ToString();
    }

    private static async Task Fuzz(string name, IReadOnlyList<string> seeds, Action<string> parse, int seed)
    {
        var rnd = new Random(seed);
        var inputs = new List<string> { "", " ", new string('{', 10_000), new string('a', 200_000) };
        inputs.AddRange(seeds);
        for (var i = 0; i < Rounds; i++)
        {
            inputs.Add(Mutate(rnd, seeds[rnd.Next(seeds.Count)]));
        }
        foreach (var input in inputs)
        {
            var sw = Stopwatch.StartNew();
            var task = Task.Run(() => parse(input));
            bool finished;
            try
            {
                finished = task.Wait(_limit);
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException!;
                Assert.Fail($"{name}: {inner.GetType().Name}: {inner.Message} at {inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim()} on input: {Show(input)}");
                return;
            }
            if (!finished)
            {
                Assert.Fail($"{name}: hang (> {_limit.TotalSeconds}s) on input: {Show(input)}");
            }
            await Assert.That(sw.Elapsed < _limit).IsTrue();
        }
    }

    private static string Show(string s) => s.Length > 300 ? s[..300] + $"… ({s.Length} chars)" : s;

    [Test]
    public async Task Fuzz_ShareLinks() =>
        await Fuzz("FmtHandler.ResolveConfig", _links, s => FmtHandler.ResolveConfig(s, out _), 1);

    [Test]
    public async Task Fuzz_ProviderLinksAndHeaders() =>
        await Fuzz("ProviderSubscription", _texts, s =>
        {
            ProviderSubscription.ExtractSubscriptionUrl(s);
            ProviderSubscription.ParseUserInfo(s);
        }, 2);

    [Test]
    public async Task Fuzz_ConnectionRules() =>
        await Fuzz("ConnectionRuleParser.Parse", _texts, s => ConnectionRuleParser.Parse(s, out _), 3);

    [Test]
    public async Task Fuzz_PublishedLists() =>
        await Fuzz("published lists", _texts, s =>
        {
            ServerNetworkCheck.ParseList(s);
            VpnStrategyManager.ParseList(s);
            ServerNetworkCheck.ParseOriginAsn(s);
            ServerNetworkCheck.ParseAsName(s);
            CustomConfigDomains.Extract(s);
            ReleaseSignature.Parse(s);
        }, 4);

    [Test]
    public async Task Fuzz_FullJsonSubscriptions() =>
        await Fuzz("V2rayFmt/SingboxFmt.ResolveToCustom", [
            """[{"remarks":"a","outbounds":[{"protocol":"vless","tag":"proxy","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"x"}]}]}}]}]""",
            """{"outbounds":[{"type":"vless","tag":"proxy","server":"b.example.com","server_port":443,"uuid":"x"}]}""",
            """{"protocol":"trojan","settings":{"servers":[{"address":"c.example.com","port":443,"password":"p"}]}}""",
        ], s =>
        {
            V2rayFmt.ResolveToCustom(s, "sub");
            SingboxFmt.ResolveToCustom(s, "sub");
            V2rayFmt.ResolveToCustomOutbound(s, "sub");
            SingboxFmt.ResolveToCustomOutbound(s, "sub");
        }, 6);

    [Test]
    public async Task CustomConfigDomains_ArrayRootAndOddShapes()
    {
        var arr = """[{"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"a.example.com"}]}}]},{"outbounds":[{"type":"vless","server":"b.example.com"}]}]""";
        await Assert.That(CustomConfigDomains.Extract(arr).OrderBy(x => x).ToList()).IsEquivalentTo(new[] { "a.example.com", "b.example.com" });
        await Assert.That(CustomConfigDomains.Extract("""{"outbounds":[{"streamSettings":"x","settings":[1]}]}""")).IsEmpty();
        await Assert.That(CustomConfigDomains.Extract("42")).IsEmpty();
    }

    [Test]
    public async Task KillSwitch_QuoteNeverLetsAPathOutOfTheString()
    {
        await Assert.That(KillSwitchManager.Quote(@"C:\Detour\Detour.exe")).IsEqualTo(@"'C:\Detour\Detour.exe'");
        await Assert.That(KillSwitchManager.Quote("C:\\O'Brien\\x.exe")).IsEqualTo("'C:\\O''Brien\\x.exe'");
        // PowerShell ends single-quoted strings at U+2018..U+201B too.
        await Assert.That(KillSwitchManager.Quote("a\u2019; Remove-Item C:\\ -Recurse #")).IsEqualTo("'a\u2019\u2019; Remove-Item C:\\ -Recurse #'");
        await Assert.That(KillSwitchManager.Quote("\u2018\u201A\u201B")).IsEqualTo("'\u2018\u2018\u201A\u201A\u201B\u201B'");
        await Assert.That(() => KillSwitchManager.Quote("a\nb")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Fuzz_ZapretBat() =>
        await Fuzz("ZapretStrategyParser.Parse", [Bat], s => ZapretStrategyParser.Parse(s, @"C:\z\bin\", @"C:\z\lists\", null, out _), 5);
}
