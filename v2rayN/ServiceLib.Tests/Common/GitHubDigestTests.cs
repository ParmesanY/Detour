namespace ServiceLib.Tests.Common;

public class GitHubDigestTests
{
    [Test]
    public async Task Digest_ParsesApiAndAssetUrls()
    {
        var hash = new string('a', 64);
        var json = $$"""{"tag_name":"1.10.4","assets":[{"name":"x.rar","digest":"sha256:{{new string('b', 64)}}"},{"name":"zapret-discord-youtube-1.10.4.zip","digest":"sha256:{{hash.ToUpperInvariant()}}"}]}""";
        await Assert.That(GitHubDigest.Find(json, "zapret-discord-youtube-1.10.4.zip")).IsEqualTo(hash);
        await Assert.That(GitHubDigest.Find(json, "missing.zip")).IsNull();
        await Assert.That(GitHubDigest.Find("""{"assets":[{"name":"a","digest":"md5:00"}]}""", "a")).IsNull();
        await Assert.That(GitHubDigest.Find("""{"assets":[{"name":"a","digest":"sha256:zz"}]}""", "a")).IsNull();
        await Assert.That(GitHubDigest.Find("not json", "a")).IsNull();
        await Assert.That(GitHubDigest.Find("[1,2]", "a")).IsNull();

        await Assert.That(GitHubDigest.RepoOf("https://github.com/Flowseal/zapret-discord-youtube")).IsEqualTo("Flowseal/zapret-discord-youtube");
        await Assert.That(GitHubDigest.RepoOf("https://example.com/a/b")).IsNull();

        var asset = GitHubDigest.ParseAssetUrl("https://github.com/XTLS/Xray-core/releases/download/v26.11.9/Xray-windows-64.zip");
        await Assert.That(asset).IsEqualTo(("https://github.com/XTLS/Xray-core", "v26.11.9", "Xray-windows-64.zip"));
        await Assert.That(GitHubDigest.ParseAssetUrl("https://mirror.example/XTLS/Xray-core/releases/download/v1/x.zip")).IsNull();
        await Assert.That(GitHubDigest.ParseAssetUrl("https://github.com/XTLS/Xray-core/releases/latest")).IsNull();
    }

    [Test]
    public async Task Digest_ChecksFiles()
    {
        var file = Path.Combine(Path.GetTempPath(), $"detour-digest-{Guid.NewGuid():N}.bin");
        File.WriteAllText(file, "abc");
        try
        {
            // SHA-256("abc")
            const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            await Assert.That(GitHubDigest.Check(file, abc)).IsNull();
            await Assert.That(GitHubDigest.Check(file, null)).IsNull();
            await Assert.That(GitHubDigest.Check(file, new string('0', 64))).IsNotNull();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
