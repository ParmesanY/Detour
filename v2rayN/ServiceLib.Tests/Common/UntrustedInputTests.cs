using System.IO.Compression;
using System.Net.Http.Headers;

namespace ServiceLib.Tests.Common;

public class UntrustedInputTests
{
    [Test]
    public async Task Zip_EntryNamesNeverLeaveTheFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "detour-zip-" + Guid.NewGuid().ToString("N"));
        await Assert.That(FileUtils.SafeEntryPath(dir, "xray.exe")).IsEqualTo(Path.Combine(Path.GetFullPath(dir), "xray.exe"));
        foreach (var bad in new[] { @"..\..\evil.dll", "../evil.dll", @"C:\Windows\evil.dll", "..", ".", "", @"\evil", "a:b" })
        {
            await Assert.That(FileUtils.SafeEntryPath(dir, bad)).IsNull();
        }

        // A zip built "on Unix" keeps the backslashes in entry.Name: the extractor must skip it, not write outside.
        Directory.CreateDirectory(dir);
        var zipPath = dir + ".zip";
        var outside = Path.Combine(Path.GetDirectoryName(dir)!, "detour-zip-escaped.txt");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("ok.txt").Open()))
                {
                    w.Write("ok");
                }
                using (var w = new StreamWriter(zip.CreateEntry(@"..\detour-zip-escaped.txt").Open()))
                {
                    w.Write("escaped");
                }
            }
            FileUtils.ZipExtractToFile(zipPath, dir, "");
            await Assert.That(File.Exists(Path.Combine(dir, "ok.txt"))).IsTrue();
            await Assert.That(File.Exists(outside)).IsFalse();
        }
        finally
        {
            File.Delete(zipPath);
            File.Delete(outside);
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Download_TextBodiesAreCapped()
    {
        var small = new StringContent("привет", Encoding.UTF8);
        await Assert.That(await DownloadService.ReadLimitedAsync(small, 1000, default)).IsEqualTo("привет");

        var bom = new ByteArrayContent([0xEF, 0xBB, 0xBF, (byte)'o', (byte)'k']);
        bom.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        await Assert.That(await DownloadService.ReadLimitedAsync(bom, 1000, default)).IsEqualTo("ok");

        await Assert.That(async () => await DownloadService.ReadLimitedAsync(new ByteArrayContent(new byte[5000]), 1000, default))
            .Throws<InvalidDataException>();
        // No Content-Length (chunked): caught while reading.
        var chunked = new StreamContent(new MemoryStream(new byte[5000]));
        await Assert.That(async () => await DownloadService.ReadLimitedAsync(chunked, 1000, default)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Subscription_FailureLogNeverHoldsTheBody()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://secret-uuid@host:443"));
        var line = SubscriptionHandler.DescribeStart(body);
        await Assert.That(line).IsEqualTo("base64-like text");
        await Assert.That(line).DoesNotContain(body[..8]);
        await Assert.That(SubscriptionHandler.DescribeStart("<html>login</html>")).Contains("HTML");
        await Assert.That(SubscriptionHandler.DescribeStart("  ")).IsEqualTo("nothing");
    }
}
