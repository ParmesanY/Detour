namespace ServiceLib.Tests.Common;

/// <summary>
/// Checks a real signed release with the updater's own code and the built-in key. Runs only when
/// DETOUR_CHECK_RELEASE_DIR points at a folder with the release archives and their .sig files.
/// </summary>
public class ReleaseSignatureLiveTests
{
    [Test]
    public async Task RealRelease_SignaturesVerifyWithBuiltInKey()
    {
        var dir = Environment.GetEnvironmentVariable("DETOUR_CHECK_RELEASE_DIR");
        if (dir.IsNullOrEmpty() || !Directory.Exists(dir))
        {
            return;
        }
        var archives = Directory.GetFiles(dir!).Where(f => f.EndsWith(".zip") || f.EndsWith(".tar.gz")).ToList();
        await Assert.That(archives.Count).IsGreaterThan(0);
        foreach (var archive in archives)
        {
            var sig = await File.ReadAllTextAsync(archive + ReleaseSignature.Suffix);
            Console.WriteLine($"{Path.GetFileName(archive)}: {AppUpdateManager.CheckSignature(archive, sig) ?? "OK"}");
            await Assert.That(AppUpdateManager.CheckSignature(archive, sig)).IsNull();
        }
    }
}
