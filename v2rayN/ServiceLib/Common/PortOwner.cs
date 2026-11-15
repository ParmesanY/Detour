using System.Runtime.InteropServices;

namespace ServiceLib.Common;

/// <summary>Which process listens on a local TCP port (Windows, IPv4).</summary>
public static class PortOwner
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    /// <summary>PID of the process listening on <paramref name="port" />, or null if nobody (or not Windows).</summary>
    public static int? ListenerPid(int port)
    {
        if (!Utils.IsWindows() || port <= 0)
        {
            return null;
        }
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
            {
                return null;
            }
            // MIB_TCPTABLE_OWNER_PID: DWORD count, then rows of 6 DWORDs:
            // state, localAddr, localPort (network byte order in the low 16 bits), remoteAddr, remotePort, pid.
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var raw = (uint)Marshal.ReadInt32(row, 8);
                var localPort = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                if (localPort == port)
                {
                    return Marshal.ReadInt32(row, 20);
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>"name.exe (C:\path\name.exe)" of a foreign listener; null if free or one of ours.</summary>
    public static string? ForeignListener(int port)
    {
        var pid = ListenerPid(port);
        if (pid is null || pid == Environment.ProcessId)
        {
            return null;
        }
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            string? path = null;
            try
            {
                path = p.MainModule?.FileName;
            }
            catch
            {
                // no access
            }
            // Our own cores live under bin\; they are stopped before a start anyway.
            if (path != null && path.StartsWith(Utils.GetBinPath(""), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return path is null ? $"{p.ProcessName}.exe (PID {pid})" : $"{p.ProcessName}.exe ({path})";
        }
        catch
        {
            return null;
        }
    }
}
