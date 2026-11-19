namespace ServiceLib.Tests.Common;

public class AppFolderAccessTests
{
    [Test]
    public async Task AppFolder_UserTempIsWritable_SystemFolderIsNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // The user's temp folder: the user can write, as in any portable folder in the profile.
        await Assert.That(AppFolderAccess.NonAdminWriters(Path.GetTempPath())).Contains("current user");
        // System32: only TrustedInstaller / administrators.
        await Assert.That(AppFolderAccess.NonAdminWriters(Environment.SystemDirectory)).IsEmpty();
        await Assert.That(AppFolderAccess.NonAdminWriters(@"Z:\no\such\dir")).IsEmpty();
    }
}
