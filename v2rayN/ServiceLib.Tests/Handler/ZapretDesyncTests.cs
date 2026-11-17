namespace ServiceLib.Tests.Handler;

public class ZapretDesyncTests
{
    private const string Bin = @"C:\zapret\bin\";

    /// <summary>A Flowseal-like strategy: QUIC, the Google list, the general list, as winws gets it.</summary>
    private static List<string> Args() =>
    [
        "--wf-tcp=80,443", "--wf-udp=443",
        "--filter-udp=443", @"--hostlist=C:\zapret\lists\list-general.txt", "--dpi-desync=fake", "--dpi-desync-repeats=6", "--new",
        "--filter-tcp=443", @"--hostlist=C:\zapret\lists\list-google.txt", "--ip-id=zero", "--dpi-desync=multisplit", "--new",
        "--filter-tcp=80,443", @"--hostlist=C:\zapret\lists\list-general.txt", @"--hostlist=C:\zapret\lists\list-general-user.txt",
        "--dpi-desync=multisplit", "--dpi-desync-split-seqovl=568", "--dpi-desync-split-pos=1",
        @"--dpi-desync-split-seqovl-pattern=C:\zapret\bin\tls_clienthello_4pda_to.bin",
    ];

    [Test]
    public async Task General_profile_is_the_tcp_443_one_with_list_general()
    {
        var args = Args();
        var range = ZapretDesync.GeneralProfile(args);
        await range.HasValue.Should().BeTrue();
        await args[range!.Value.Start].Should().BeEqualTo("--filter-tcp=80,443");
        await range.Value.End.Should().BeEqualTo(args.Count);
    }

    [Test]
    public async Task Apply_replaces_only_the_general_profile_desync()
    {
        var result = ZapretDesync.Apply(Args(), ["--dpi-desync=fake", "--dpi-desync-fooling=ts", "--dpi-desync-fake-tls=%BIN%tls_clienthello_www_google_com.bin"], Bin)!;
        await result.Should().NotBeNull();
        var (start, end) = ZapretDesync.GeneralProfile(result)!.Value;
        var general = result.Skip(start).Take(end - start).ToList();
        await general.Should().Contain("--dpi-desync-fooling=ts");
        await general.Should().Contain(@"--dpi-desync-fake-tls=C:\zapret\bin\tls_clienthello_www_google_com.bin");
        await general.Should().NotContain("--dpi-desync=multisplit");
        await general.Should().Contain(@"--hostlist=C:\zapret\lists\list-general-user.txt");
        // QUIC and Google profiles stay as they were.
        await result.Take(start).SequenceEqual(Args().Take(start)).Should().BeTrue();
    }

    [Test]
    public async Task Apply_without_a_general_profile_is_null()
    {
        await ZapretDesync.Apply(["--filter-udp=443", "--dpi-desync=fake"], ["--dpi-desync=fake"], Bin).Should().BeNull();
    }

    [Test]
    public async Task Extract_takes_the_general_profile_desync()
    {
        var desync = ZapretDesync.Extract(Args());
        await desync.Should().HaveCount(4);
        await desync[0].Should().BeEqualTo("--dpi-desync=multisplit");
    }

    [Test]
    public async Task Tokens_accept_registry_args_and_refuse_anything_else()
    {
        var ok = ZapretDesync.Tokens("--dpi-desync=fake --dpi-desync-fooling=ts --dpi-desync-repeats=6");
        await ok.Should().NotBeNull();
        await ok!.Should().Contain("--dpi-desync-fooling=ts");
        await ZapretDesync.Tokens("--dpi-desync=fake --hostlist-auto=C:\\x.txt").Should().NotBeNull(); // context dropped
        await ZapretDesync.Tokens("--dpi-desync=fake --debug=@C:\\log.txt").Should().BeNull();
        await ZapretDesync.Tokens(null).Should().BeNull();
    }
}
