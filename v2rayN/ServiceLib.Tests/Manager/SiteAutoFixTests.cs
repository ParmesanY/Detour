namespace ServiceLib.Tests.Manager;

public class SiteAutoFixTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 12, 0, 0);
    /// <summary>Three other sites that work, so the network itself is fine.</summary>
    private static SiteAutoFixManager.Detector.Sample[] Healthy(string p) => [S(p + "h1", "ok1.com", 500, 9000), S(p + "h2", "ok2.com", 500, 9000), S(p + "h3", "ok3.com", 500, 9000)];

    private static SiteAutoFixManager.Detector.Sample S(string id, string? host, long up, long down, bool blocked = false) => new(id, host, up, down, blocked);

    [Test]
    public async Task ThreeFailedConnections_NoSuccess_Reported()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [.. Healthy("a"), S("1", "bank.ru", 500, 0), S("2", "bank.ru", 500, 300)]);
        d.Observe(T0.AddSeconds(5), [S("3", "bank.ru", 500, 0)]); // 1 and 2 closed with almost nothing back
        var result = d.Observe(T0.AddSeconds(10), []); // 3 closed too
        await Assert.That(result).IsEquivalentTo(new[] { "bank.ru" });
        // not again the same day
        d.Observe(T0.AddSeconds(15), [S("4", "bank.ru", 500, 0)]);
        await Assert.That(d.Observe(T0.AddSeconds(20), [])).IsEmpty();
    }

    [Test]
    public async Task EverythingFailing_IsTheNetwork_NotReported()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [S("1", "google.com", 500, 0), S("2", "google.com", 500, 0), S("3", "google.com", 500, 0), S("4", "x.com", 500, 0)]);
        await Assert.That(d.Observe(T0.AddSeconds(5), [])).IsEmpty();
    }

    [Test]
    public async Task OneSuccess_Clears()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [S("1", "site.com", 500, 0), S("2", "site.com", 500, 0), S("3", "site.com", 500, 0), S("4", "site.com", 500, 90_000)]);
        await Assert.That(d.Observe(T0.AddSeconds(5), [])).IsEmpty();
    }

    [Test]
    public async Task StuckOpenConnections_Count_IdleAndBlockedDont()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [.. Healthy("a"), S("1", "x.com", 600, 0), S("2", "x.com", 600, 0), S("3", "x.com", 600, 0), S("idle", "y.com", 0, 0), S("ad", "ads.net", 500, 0, blocked: true)]);
        var result = d.Observe(T0.AddSeconds(16), [S("1", "x.com", 600, 0), S("2", "x.com", 600, 0), S("3", "x.com", 600, 0), S("idle", "y.com", 0, 0), S("ad", "ads.net", 500, 0, blocked: true)]);
        await Assert.That(result).IsEquivalentTo(new[] { "x.com" });
    }

    [Test]
    public async Task ShortAnswers_AndNoHost_AreNotFailures()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [S("1", "cdn.com", 500, 2000), S("2", "cdn.com", 500, 2000), S("3", "cdn.com", 500, 2000), S("4", null, 500, 0)]);
        await Assert.That(d.Observe(T0.AddSeconds(5), [])).IsEmpty();
    }

    [Test]
    public async Task OldFailures_LeaveTheWindow()
    {
        var d = new SiteAutoFixManager.Detector();
        d.Observe(T0, [S("1", "a.ru", 500, 0), S("2", "a.ru", 500, 0)]);
        d.Observe(T0.AddSeconds(1), []);
        d.Observe(T0.AddMinutes(4), [S("3", "a.ru", 500, 0)]);
        await Assert.That(d.Observe(T0.AddMinutes(4).AddSeconds(1), [])).IsEmpty();
    }
}
