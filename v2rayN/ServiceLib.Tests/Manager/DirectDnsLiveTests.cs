namespace ServiceLib.Tests.Manager;

/// <summary>Real queries to the public resolvers; runs only with DETOUR_LIVE_DNS=1 (network needed).</summary>
public class DirectDnsLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DETOUR_LIVE_DNS") == "1";

    [Test]
    public async Task EveryEndpoint_Answers()
    {
        if (!Enabled)
        {
            return;
        }
        var failed = new List<string>();
        foreach (var e in DirectDns.Endpoints)
        {
            if (!await DirectDns.CheckAsync(e, null, CancellationToken.None))
            {
                failed.Add(e.ToString());
            }
        }
        await Assert.That(string.Join(", ", failed)).IsEqualTo("");
        await Assert.That(await DirectDns.TxtAsync("8.8.8.8.origin.asn.cymru.com", null, CancellationToken.None)).Contains("15169");
    }
}
