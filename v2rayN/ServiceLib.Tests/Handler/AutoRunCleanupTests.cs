using System.Net.Sockets;

namespace ServiceLib.Tests.Handler;

public class AutoRunCleanupTests
{
    [Test]
    public async Task TaskCommand_ReadsExePathFromTaskXml()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task><Actions Context="Author"><Exec>
              <Command>"D:\Projects\vpn-client\publish\win-x64\v2rayN.exe"</Command>
              <WorkingDirectory>D:\Projects\vpn-client\publish\win-x64</WorkingDirectory>
            </Exec></Actions></Task>
            """;

        await Assert.That(AutoStartupHandler.TaskCommand(xml)).IsEqualTo(@"D:\Projects\vpn-client\publish\win-x64\v2rayN.exe");
        await Assert.That(AutoStartupHandler.TaskCommand("<Task/>")).IsNull();
    }

    [Test]
    public async Task IsOurOldBuild_OnlyFoldersWithZapret()
    {
        var root = Path.Combine(Path.GetTempPath(), "detour-test-" + Guid.NewGuid().ToString("N"));
        var ours = Path.Combine(root, "old");
        var upstream = Path.Combine(root, "upstream");
        Directory.CreateDirectory(Path.Combine(ours, "bin", "zapret"));
        Directory.CreateDirectory(Path.Combine(upstream, "bin", "xray"));
        try
        {
            await Assert.That(AutoStartupHandler.IsOurOldBuild(Path.Combine(ours, "v2rayN.exe"))).IsTrue();
            await Assert.That(AutoStartupHandler.IsOurOldBuild($"\"{Path.Combine(ours, "v2rayN.exe")}\"")).IsTrue();
            await Assert.That(AutoStartupHandler.IsOurOldBuild(Path.Combine(upstream, "v2rayN.exe"))).IsFalse();
            await Assert.That(AutoStartupHandler.IsOurOldBuild(null)).IsFalse();
            await Assert.That(AutoStartupHandler.IsOurOldBuild(Utils.GetExePath())).IsFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task PortOwner_FindsListenerPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await Assert.That(PortOwner.ListenerPid(port)).IsEqualTo(Environment.ProcessId);
            // Our own process is never "foreign".
            await Assert.That(PortOwner.ForeignListener(port)).IsNull();
        }
        finally
        {
            listener.Stop();
        }
    }
}
