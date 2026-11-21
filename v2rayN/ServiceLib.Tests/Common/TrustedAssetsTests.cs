using System.Security.Cryptography;

namespace ServiceLib.Tests.Common;

public class TrustedAssetsTests
{
    private const string Zapret = "https://github.com/Flowseal/zapret-discord-youtube";

    private static string List(string hash) => $$"""
        {
          "version": 1,
          "assets": {
            "{{Zapret}}/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip": "{{hash}}",
            "https://github.com/Flowseal/tg-ws-proxy/releases/download/v1.11.4/TgWsProxy_windows.exe": "{{new string('c', 64)}}",
            "bad": "not-a-hash"
          }
        }
        """;

    [Test]
    public async Task TrustedList_OnlySignedListsAndListedHashesInstall()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        string Sign(string text) => Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        var file = Path.Combine(Path.GetTempPath(), $"detour-trusted-{Guid.NewGuid():N}.zip");
        File.WriteAllText(file, "abc");
        const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        try
        {
            var json = List(abc);
            var list = TrustedAssets.Verify(json, Sign(json), pub);
            await Assert.That(list).IsNotNull();
            await Assert.That(list!.Count).IsEqualTo(2);

            // Changed after signing, or signed by someone else: no list at all.
            await Assert.That(TrustedAssets.Verify(json.Replace(abc, new string('0', 64)), Sign(json), pub)).IsNull();
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await Assert.That(TrustedAssets.Verify(json, Sign(json), Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()))).IsNull();
            await Assert.That(TrustedAssets.Verify(json, null, pub)).IsNull();

            var url = TrustedAssets.Url(Zapret, "1.10.4", "zapret-discord-youtube-1.10.4.zip");
            await Assert.That(TrustedAssets.Check(list, url, file)).IsNull();
            File.WriteAllText(file, "abd");
            await Assert.That(TrustedAssets.Check(list, url, file)).IsEqualTo(AppText.TrustedMismatch);
            // A newer upstream release nobody checked yet, or no list: fail closed.
            await Assert.That(TrustedAssets.Check(list, TrustedAssets.Url(Zapret, "1.10.5", "zapret-discord-youtube-1.10.5.zip"), file))
                .IsEqualTo(string.Format(AppText.TrustedNotListed, "1.10.5/zapret-discord-youtube-1.10.5.zip"));
            await Assert.That(TrustedAssets.Check(null, url, file)).IsEqualTo(AppText.TrustedListUnavailable);

            await Assert.That(TrustedAssets.TagFor(list, Zapret, ".zip")).IsEqualTo("1.10.4");
            await Assert.That(TrustedAssets.TagFor(list, "https://github.com/Flowseal/tg-ws-proxy", "TgWsProxy_windows.exe")).IsEqualTo("v1.11.4");
            await Assert.That(TrustedAssets.TagFor(list, "https://github.com/bol-van/zapret", ".zip")).IsNull();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task TrustedList_PublishedListIsSignedByTheReleaseKey()
    {
        // Once signed, the copy in the repo must verify with the built-in key and cover what cores.lock.json pins.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "tools", "cores.lock.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        var path = dir is null ? null : Path.Combine(dir, TrustedAssets.ListPath);
        if (path is null || !File.Exists(path))
        {
            return;
        }
        var list = TrustedAssets.Verify(File.ReadAllText(path), File.ReadAllText(path + ReleaseSignature.Suffix));
        await Assert.That(list).IsNotNull();
        var lockJson = JsonUtils.ParseJson(File.ReadAllText(Path.Combine(dir!, "tools", "cores.lock.json")))!.AsObject();
        foreach (var (_, pin) in lockJson)
        {
            // Tor (no "repo": not on GitHub) only ships in the archive; the app never downloads it.
            if (pin!["repo"] is null)
            {
                continue;
            }
            foreach (var (name, hash) in pin!["sha256"]!.AsObject())
            {
                var url = TrustedAssets.Url($"https://github.com/{pin["repo"]}", pin["tag"]!.ToString(), name);
                await Assert.That(list!.GetValueOrDefault(url)).IsEqualTo(hash!.ToString());
            }
        }
    }
}
