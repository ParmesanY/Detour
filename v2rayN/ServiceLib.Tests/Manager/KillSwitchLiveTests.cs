using System.Diagnostics;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// The kill switch for real: changes the Windows Firewall. Runs only on a CI runner (DETOUR_LIVE_KILLSWITCH=1,
/// elevated; job "killswitch" in build.yml) and does nothing anywhere else, never on a developer's PC.
/// curl.exe stands for "any other program": it isn't on the allowed list (the test host itself is, as the client).
/// </summary>
public class KillSwitchLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DETOUR_LIVE_KILLSWITCH") == "1"
                                   && OperatingSystem.IsWindows() && Utils.IsAdministrator();

    private static async Task<bool> CurlReachesInternet()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-s", "-o", "NUL", "-m", "8", "https://1.1.1.1/" })
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }

    private static async Task<string> OutboundActions()
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command", "(Get-NetFirewallProfile | ForEach-Object { \"$($_.Name)=$($_.DefaultOutboundAction)\" }) -join ','" })
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Trim();
    }

    [Test]
    public async Task KillSwitch_Live_BlocksOtherProgramsAndRestores()
    {
        if (!Enabled)
        {
            return;
        }
        var ks = KillSwitchManager.Instance;
        await ks.ReleaseAsync();
        var before = await OutboundActions();
        await Assert.That(await CurlReachesInternet()).IsTrue();

        try
        {
            await Assert.That(await ks.EngageAsync()).IsNull();
            await Assert.That(ks.IsEngaged).IsTrue();
            // Another program on the PC must not get out while the VPN core is down.
            await Assert.That(await CurlReachesInternet()).IsFalse();
        }
        finally
        {
            await ks.ReleaseAsync();
        }

        await Assert.That(ks.IsEngaged).IsFalse();
        await Assert.That(await OutboundActions()).IsEqualTo(before);
        await Assert.That(await CurlReachesInternet()).IsTrue();
    }

    [Test]
    public async Task KillSwitch_Live_BlockLeftByACrashIsReleased()
    {
        if (!Enabled)
        {
            return;
        }
        var ks = KillSwitchManager.Instance;
        await ks.ReleaseAsync();
        var before = await OutboundActions();
        await Assert.That(await ks.EngageAsync()).IsNull();

        // A client crash leaves the rules and the state file behind; the next start releases them from the file alone.
        var rulesLeft = await OutboundActions();
        await Assert.That(rulesLeft).IsNotEqualTo(before);
        await ks.ReleaseAsync();

        await Assert.That(await OutboundActions()).IsEqualTo(before);
        await Assert.That(await CurlReachesInternet()).IsTrue();
    }
}
