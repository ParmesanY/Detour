namespace ServiceLib.Tests.Manager;

public class IntegrityCheckTests
{
    [Test]
    public async Task Integrity_FileThatWasThereAndIsGone()
    {
        var disk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\D\bin\xray\xray.exe", @"C:\D\bin\zapret\current\bin\winws.exe" };

        // First start: nothing remembered yet, nothing reported; what's on disk is remembered.
        var (missing, remember) = IntegrityCheck.Compare([], disk, disk.Contains);
        await Assert.That(missing).IsEmpty();
        await Assert.That(remember.Count).IsEqualTo(2);

        // The antivirus took winws.exe: reported, and kept in memory so the warning comes back until it's restored.
        disk.Remove(@"C:\D\bin\zapret\current\bin\winws.exe");
        (missing, remember) = IntegrityCheck.Compare(remember, disk, disk.Contains);
        await Assert.That(missing).IsEquivalentTo(new[] { @"C:\D\bin\zapret\current\bin\winws.exe" });
        await Assert.That(remember.Count).IsEqualTo(2);

        // Restored: quiet again. A file never seen before is never "missing".
        disk.Add(@"C:\D\bin\zapret\current\bin\winws.exe");
        (missing, _) = IntegrityCheck.Compare(remember, disk, disk.Contains);
        await Assert.That(missing).IsEmpty();
        (missing, _) = IntegrityCheck.Compare([], [], _ => false);
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task Integrity_ExclusionScriptQuotesThePath()
    {
        var script = IntegrityCheck.ExclusionScript(@"C:\Users\O’Brien\Detour\bin");
        await Assert.That(script).Contains(@"Add-MpPreference -ExclusionPath 'C:\Users\O’’Brien\Detour\bin'");
        await Assert.That(script).Contains("no-defender");
    }
}
