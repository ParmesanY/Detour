using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ServiceLib.Common;

/// <summary>
/// Detour runs elevated and starts xray.exe, sing-box.exe, winws.exe and its own DLLs from its folder. If the
/// folder is writable without admin rights (any portable folder in the user's profile is), a program running
/// as that user without elevation can replace one of those files and get admin rights at the next start.
/// Program Files (writable only by administrators) closes it; here we only tell the user.
/// </summary>
public static class AppFolderAccess
{
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Accounts that can write there besides administrators and the system; empty when it's safe or unknown.</summary>
    public static List<string> NonAdminWriters(string dir)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(dir))
        {
            return [];
        }
        try
        {
            return Writers(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> Writers(string dir)
    {
        var acl = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
        var me = WindowsIdentity.GetCurrent().User;
        var result = new List<string>();
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0
                || rule.IdentityReference is not SecurityIdentifier sid || IsTrusted(sid))
            {
                continue;
            }
            // Account names, never the user's own name: the line goes into the help archive.
            var name = sid == me ? "current user"
                : sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ? "Users"
                : sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ? "Authenticated Users"
                : sid.IsWellKnown(WellKnownSidType.WorldSid) ? "Everyone"
                : sid.IsWellKnown(WellKnownSidType.InteractiveSid) ? "Interactive"
                : "another account";
            if (!result.Contains(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
        // NT SERVICE\TrustedInstaller
        || sid.Value.StartsWith("S-1-5-80-956008885-", StringComparison.Ordinal);
}
