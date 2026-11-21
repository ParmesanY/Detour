namespace ServiceLib.Tests.Manager;

public class DirectDnsTests
{
    /// <summary>A response to <paramref name="query"/> with the given flags and answer records.</summary>
    private static byte[] Response(byte[] query, byte flags2, byte flags3, params (ushort Type, byte[] Data)[] answers)
    {
        var r = new List<byte>(query);
        r[2] = flags2;
        r[3] = flags3;
        r[7] = (byte)answers.Length;
        foreach (var (type, data) in answers)
        {
            r.AddRange([0xC0, 12, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 1, 0, (byte)(data.Length >> 8), (byte)data.Length]);
            r.AddRange(data);
        }
        return [.. r];
    }

    [Test]
    public async Task Parse_ARecords_SkipsBogus()
    {
        var q = DirectDns.BuildQuery("www.google.com", DirectDns.RecordType.A, 7);
        var r = Response(q, 0x81, 0x80, (1, [142, 250, 1, 1]), (1, [10, 0, 0, 1]), (5, [0]), (1, [198, 18, 0, 5]));
        await Assert.That(DirectDns.ParseResponse(r, DirectDns.RecordType.A)).IsEquivalentTo(new[] { "142.250.1.1" });
    }

    [Test]
    public async Task Parse_Txt()
    {
        var q = DirectDns.BuildQuery("1.2.3.4.origin.asn.cymru.com", DirectDns.RecordType.Txt, 9);
        var text = "15169 | 8.8.8.0/24 | US"u8.ToArray();
        var r = Response(q, 0x81, 0x80, (16, [(byte)text.Length, .. text]));
        await Assert.That(DirectDns.ParseResponse(r, DirectDns.RecordType.Txt)[0]).IsEqualTo("15169 | 8.8.8.0/24 | US");
    }

    [Test]
    public async Task Parse_RejectsInterceptionAndErrors()
    {
        var q = DirectDns.BuildQuery("www.google.com", DirectDns.RecordType.A, 7);
        // AA flag from a "public recursive resolver": the ISP's NSDI answering in its place
        await Assert.That(() => DirectDns.ParseResponse(Response(q, 0x85, 0x80, (1, [1, 2, 3, 4])), DirectDns.RecordType.A)).Throws<InvalidDataException>();
        // NXDOMAIN
        await Assert.That(() => DirectDns.ParseResponse(Response(q, 0x81, 0x83), DirectDns.RecordType.A)).Throws<InvalidDataException>();
        // the query echoed back, not a response
        await Assert.That(() => DirectDns.ParseResponse(q, DirectDns.RecordType.A)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Pick_PrefersOrder_CoreDefaultIsDoh()
    {
        var tcpGoogle = DirectDns.Endpoints.First(e => e.Kind == DirectDns.Kind.Tcp);
        var quad9 = DirectDns.Endpoints.First(e => e.Name == "Quad9" && e.Kind == DirectDns.Kind.Doh);
        await Assert.That(DirectDns.Pick([tcpGoogle, quad9])).IsEqualTo(quad9);
        await Assert.That(DirectDns.Pick([])).IsNull();
        await Assert.That(DirectDns.Endpoints.Where(e => !e.ForCore).All(e => e.Host != null)).IsTrue();
    }
}
