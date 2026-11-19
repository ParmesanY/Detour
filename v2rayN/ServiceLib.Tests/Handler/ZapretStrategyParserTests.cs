namespace ServiceLib.Tests.Handler;

public class ZapretStrategyParserTests
{
    private const string BinDir = @"C:\app\bin\zapret\current\bin";
    private const string ListsDir = @"C:\app\bin\zapret\current\lists";

    // Shortened copy of Flowseal zapret-discord-youtube 1.10.4 general.bat (MIT).
    private const string GeneralBat = """
        @echo off
        chcp 65001 > nul
        :: 65001 - UTF-8

        cd /d "%~dp0"
        call service.bat status_zapret
        call service.bat load_user_lists
        echo:

        set "BIN=%~dp0bin\"
        set "LISTS=%~dp0lists\"
        cd /d %BIN%

        start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
        --filter-udp=443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --dpi-desync=fake --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
        --filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --dpi-desync=multisplit --new ^
        --filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --dpi-desync=multisplit --dpi-desync-cutoff=n3
        """;

    [Test]
    public async Task Parse_GeneralBat_ExpandsPathsAndJoinsContinuationLines()
    {
        // Windows paths and winws-only behaviour (Linux and macOS use tpws).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var args = ZapretStrategyParser.Parse(GeneralBat, BinDir, ListsDir, "disabled", out var error);

        await args.Should().NotBeNull().Because(error);
        await args![0].Should().BeEqualTo("--wf-tcp=80,443,2053,12");
        await args[1].Should().BeEqualTo("--wf-udp=443,19294-19344,50000-50100,12");
        await args.Should().Contain(@"--hostlist=C:\app\bin\zapret\current\lists\list-general-user.txt");
        await args.Should().Contain(@"--dpi-desync-fake-quic=C:\app\bin\zapret\current\bin\quic_initial_www_google_com.bin");
        await args[^1].Should().BeEqualTo("--dpi-desync-cutoff=n3");
        await args.Should().NotContain("^");
        await args.Where(a => a.Contains('%') || a.Contains('"')).Should().BeEmpty();
    }

    [Test]
    public async Task Parse_AddsClientIpsetNextToEveryIpsetAll()
    {
        // Windows paths and winws-only behaviour (Linux and macOS use tpws).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var args = ZapretStrategyParser.Parse(GeneralBat, BinDir, ListsDir, "disabled", out _)!;

        var userIpset = $@"--ipset={ListsDir}\{ZapretStrategyParser.UserIpsetFile}";
        await args.Count(a => a == userIpset).Should().BeEqualTo(2);
        var first = args.IndexOf(@"--ipset=C:\app\bin\zapret\current\lists\ipset-all.txt");
        await args[first + 1].Should().BeEqualTo(userIpset);
    }

    [Test]
    [Arguments("disabled", "12", "12")]
    [Arguments("all", "1024-65535", "1024-65535")]
    [Arguments("tcp", "1024-65535", "12")]
    [Arguments("udp", "12", "1024-65535")]
    [Arguments(null, "12", "12")]
    public async Task Parse_GameFilterModes_FillPortRanges(string? mode, string tcp, string udp)
    {
        var args = ZapretStrategyParser.Parse(GeneralBat, BinDir, ListsDir, mode, out _)!;

        await args[0].Should().EndWith("," + tcp);
        await args[1].Should().EndWith("," + udp);
        await args.Should().Contain("--filter-tcp=" + tcp);
    }

    [Test]
    public async Task Parse_CaretEscape_IsRemovedOutsideQuotes()
    {
        const string bat = "start \"z\" /min \"%BIN%winws.exe\" --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! \"--x=a^b\"\r\n";
        var args = ZapretStrategyParser.Parse(bat, BinDir, ListsDir, null, out var error)!;

        await args.Should().NotBeNull().Because(error);
        await args.Should().Contain("--dpi-desync-fake-tls=!");
        await args.Should().Contain("--x=a^b");
    }

    [Test]
    public async Task Parse_NoWinws_ReturnsError()
    {
        var args = ZapretStrategyParser.Parse("@echo off\r\necho hello\r\n", BinDir, ListsDir, null, out var error);

        await args.Should().BeNull();
        await error.Should().Contain("winws.exe");
    }

    [Test]
    public async Task Parse_UnknownVariable_ReturnsError()
    {
        const string bat = "\"%BIN%winws.exe\" --wf-tcp=%SomethingElse% --new";
        var args = ZapretStrategyParser.Parse(bat, BinDir, ListsDir, null, out var error);

        await args.Should().BeNull();
        await error.Should().Contain("%SomethingElse%");
    }

    [Test]
    public async Task Parse_CrLfLineEndings_Work()
    {
        var args = ZapretStrategyParser.Parse(GeneralBat.Replace("\n", "\r\n"), BinDir, ListsDir, null, out var error);

        await args.Should().NotBeNull().Because(error);
        await args![^1].Should().BeEqualTo("--dpi-desync-cutoff=n3");
    }

    [Test]
    public async Task SortStrategies_NumericAltThenFakeThenExp()
    {
        var shuffled = new[]
        {
            "general (EXP).bat", "general (ALT10).bat", "general (SIMPLE FAKE ALT).bat", "general (ALT2).bat",
            "general (FAKE TLS AUTO).bat", "general.bat", "general (FAKE TLS AUTO ALT2).bat", "general (ALT).bat",
            "general (SIMPLE FAKE).bat", "general (FAKE TLS AUTO ALT).bat", "general (ALT13).bat", "general (ALT3).bat",
        };

        var sorted = ZapretStrategyParser.SortStrategies(shuffled);

        await sorted.Should().BeEquivalentTo(new List<string>
        {
            "general.bat", "general (ALT).bat", "general (ALT2).bat", "general (ALT3).bat", "general (ALT10).bat",
            "general (ALT13).bat", "general (FAKE TLS AUTO).bat", "general (FAKE TLS AUTO ALT).bat",
            "general (FAKE TLS AUTO ALT2).bat", "general (SIMPLE FAKE).bat", "general (SIMPLE FAKE ALT).bat",
            "general (EXP).bat",
        });
        await string.Join("|", sorted).Should().BeEqualTo(string.Join("|", new[]
        {
            "general.bat", "general (ALT).bat", "general (ALT2).bat", "general (ALT3).bat", "general (ALT10).bat",
            "general (ALT13).bat", "general (FAKE TLS AUTO).bat", "general (FAKE TLS AUTO ALT).bat",
            "general (FAKE TLS AUTO ALT2).bat", "general (SIMPLE FAKE).bat", "general (SIMPLE FAKE ALT).bat",
            "general (EXP).bat",
        }));
    }

    [Test]
    public async Task ListStrategies_DefaultFirstAndIgnoresServiceBat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zapret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in new[] { "service.bat", "general (ALT2).bat", "general.bat", "general (ALT).bat" })
            {
                File.WriteAllText(Path.Combine(dir, n), "");
            }

            var list = ZapretStrategyParser.ListStrategies(dir);

            await list.Should().HaveCount(3);
            await list[0].Should().BeEqualTo("general.bat");
            await list.Should().NotContain("service.bat");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
