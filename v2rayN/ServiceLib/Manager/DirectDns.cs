using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ServiceLib.Manager;

/// <summary>
/// DNS past the ISP and past the TUN, for what Detour itself needs to resolve directly: VPN server names under
/// TUN (the cores' core-dns), the watchdog's and auto-select's checks, site checks, AS lookups.
/// Since August 2026 several ISPs (Rostelecom, Dom.ru, Beeline, MTS…) break DoH/DoT to Google and Cloudflare,
/// and the ISP DNS (NSDI) answers stubs or intercepts UDP/53. So there's no single resolver to rely on: a
/// cascade of endpoints is tried over the physical card and the first one that answers sanely is remembered.
/// DoH (RFC 8484 wire format) to several providers by IP, then plain DNS over TCP/53 (UDP/53 is the one
/// intercepted), rejecting answers with the AA flag: a public recursive resolver never sets it, an
/// interceptor pretending to be one does.
/// </summary>
public static class DirectDns
{
    private const string _tag = "DirectDns";

    public enum Kind { Doh, Tcp }

    /// <param name="Host">The TLS name for DoH (null: the IP itself, no SNI sent).</param>
    /// <param name="ForCore">Whether the cores can use it as core-dns: by IP, no SNI.</param>
    public sealed record Endpoint(string Name, Kind Kind, IPAddress Ip, string? Host = null, bool ForCore = true)
    {
        public override string ToString() => Kind == Kind.Doh ? $"{Name} DoH {Ip}" : $"{Name} TCP {Ip}:53";
    }

    /// <summary>In order of preference; the remembered working one is tried first.</summary>
    public static readonly IReadOnlyList<Endpoint> Endpoints =
    [
        new("Google", Kind.Doh, IPAddress.Parse("8.8.8.8")),
        new("Cloudflare", Kind.Doh, IPAddress.Parse("1.1.1.1")),
        new("Quad9", Kind.Doh, IPAddress.Parse("9.9.9.9")),
        new("AdGuard", Kind.Doh, IPAddress.Parse("94.140.14.140"), "unfiltered.adguard-dns.com", ForCore: false),
        new("Google", Kind.Tcp, IPAddress.Parse("8.8.8.8")),
        new("Cloudflare", Kind.Tcp, IPAddress.Parse("1.1.1.1")),
        new("Quad9", Kind.Tcp, IPAddress.Parse("9.9.9.9")),
    ];

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(30);
    private const string ProbeName = "www.google.com";

    private static volatile Endpoint? _best;
    private static DateTime _probedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _probeLock = new(1, 1);

    /// <summary>The endpoint the cores use for VPN server names (core-dns): the best one they can use.</summary>
    public static Endpoint CoreEndpoint => _bestCore ?? Endpoints[0];

    private static volatile Endpoint? _bestCore;

    /// <summary>Re-checks the endpoints when the last check is older than 30 minutes (or forced, after a network change).</summary>
    public static async Task EnsureProbedAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && DateTime.Now - _probedAt < ProbeEvery)
        {
            return;
        }
        await _probeLock.WaitAsync(ct);
        try
        {
            if (!force && DateTime.Now - _probedAt < ProbeEvery)
            {
                return;
            }
            var local = ZapretWatchdog.PhysicalIPv4Address();
            var results = await Task.WhenAll(Endpoints.Select(async e =>
            {
                try
                {
                    return (e, Ok: (await QueryAsync(e, ProbeName, RecordType.A, local, ct)).Count > 0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return (e, Ok: false);
                }
            }));
            _probedAt = DateTime.Now;
            var working = results.Where(r => r.Ok).Select(r => r.e).ToList();
            var best = Pick(working);
            if (best != null)
            {
                _best = best;
                _bestCore = Pick(working.Where(e => e.ForCore).ToList()) ?? _bestCore;
            }
            Logging.SaveLog($"{_tag}: {working.Count}/{Endpoints.Count} answer; using {best?.ToString() ?? "none (ISP resolver)"}; "
                            + $"failed: {string.Join(", ", results.Where(r => !r.Ok).Select(r => r.e.ToString()))}");
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>Whether one endpoint answers the probe name right now (diagnostics, tests).</summary>
    public static async Task<bool> CheckAsync(Endpoint e, IPAddress? local, CancellationToken ct)
    {
        try
        {
            return (await QueryAsync(e, ProbeName, RecordType.A, local, ct)).Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The first working endpoint in preference order.</summary>
    public static Endpoint? Pick(IReadOnlyCollection<Endpoint> working)
        => Endpoints.FirstOrDefault(working.Contains);

    public enum RecordType : ushort { A = 1, Txt = 16 }

    /// <summary>
    /// A records of <paramref name="host"/> over the physical card: the remembered endpoint first, then the rest.
    /// Empty when none answers (the caller falls back to the system resolver).
    /// </summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, IPAddress? local, CancellationToken ct)
    {
        foreach (var e in Order())
        {
            try
            {
                var answers = await QueryAsync(e, host, RecordType.A, local, ct);
                var ips = answers.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).Where(ip => ip != null).Select(ip => ip!).ToArray();
                if (ips.Length > 0)
                {
                    Remember(e);
                    return ips;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // next endpoint
            }
        }
        return [];
    }

    /// <summary>The first TXT record of a name (AS lookups), or null.</summary>
    public static async Task<string?> TxtAsync(string name, IPAddress? local, CancellationToken ct)
    {
        foreach (var e in Order())
        {
            try
            {
                var answers = await QueryAsync(e, name, RecordType.Txt, local, ct);
                if (answers.Count > 0)
                {
                    Remember(e);
                    return answers[0];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    private static IEnumerable<Endpoint> Order()
    {
        var best = _best;
        return best == null ? Endpoints : Endpoints.Where(e => e == best).Concat(Endpoints.Where(e => e != best));
    }

    private static void Remember(Endpoint e)
    {
        if (e.ForCore)
        {
            _bestCore = e;
        }
        if (_best != e)
        {
            _best = e;
            Logging.SaveLog($"{_tag}: now using {e}");
        }
    }

    // ---- queries ----

    private static async Task<List<string>> QueryAsync(Endpoint e, string name, RecordType type, IPAddress? local, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(QueryTimeout);
        var query = BuildQuery(name, type, (ushort)Random.Shared.Next(1, ushort.MaxValue));
        var response = e.Kind == Kind.Doh
            ? await DohAsync(e, query, local, cts.Token)
            : await TcpAsync(e, query, local, cts.Token);
        return ParseResponse(response, type);
    }

    private static async Task<byte[]> DohAsync(Endpoint e, byte[] query, IPAddress? local, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (ctx, c) => new ValueTask<Stream>(ZapretWatchdog.ConnectBoundAsync([e.Ip], ctx.DnsEndPoint.Port, local, c)),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var dns = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // HTTP/2: Quad9 answers HTTP/1.1 with 505.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{e.Host ?? e.Ip.ToString()}/dns-query?dns={dns}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        req.Headers.Accept.ParseAdd("application/dns-message");
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task<byte[]> TcpAsync(Endpoint e, byte[] query, IPAddress? local, CancellationToken ct)
    {
        await using var stream = await ZapretWatchdog.ConnectBoundAsync([e.Ip], 53, local, ct);
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct);
        var head = new byte[2];
        await stream.ReadExactlyAsync(head, ct);
        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(head)];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    // ---- wire format (RFC 1035) ----

    public static byte[] BuildQuery(string name, RecordType type, ushort id)
    {
        var bytes = new List<byte>(32 + name.Length);
        bytes.AddRange([(byte)(id >> 8), (byte)(id & 0xFF), 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]); // RD, 1 question
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var l = System.Text.Encoding.ASCII.GetBytes(label);
            if (l.Length is 0 or > 63)
            {
                throw new ArgumentException("bad name", nameof(name));
            }
            bytes.Add((byte)l.Length);
            bytes.AddRange(l);
        }
        bytes.AddRange([0, (byte)((ushort)type >> 8), (byte)((ushort)type & 0xFF), 0, 1]);
        return [.. bytes];
    }

    /// <summary>
    /// Records of the asked type: IPv4 addresses as text, or the joined strings of a TXT record. Throws on a
    /// malformed answer, an error code, or the AA flag (an interceptor posing as a public resolver).
    /// </summary>
    public static List<string> ParseResponse(byte[] r, RecordType type)
    {
        if (r.Length < 12 || (r[2] & 0x80) == 0)
        {
            throw new InvalidDataException("not a DNS response");
        }
        if ((r[2] & 0x04) != 0)
        {
            throw new InvalidDataException("authoritative answer from a recursive resolver: intercepted");
        }
        if ((r[3] & 0x0F) != 0)
        {
            throw new InvalidDataException($"rcode {r[3] & 0x0F}");
        }
        var qd = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4));
        var an = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6));
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(r, pos) + 4;
        }
        var result = new List<string>();
        for (var i = 0; i < an; i++)
        {
            pos = SkipName(r, pos);
            var rtype = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(pos));
            var len = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(pos + 8));
            var data = pos + 10;
            if (data + len > r.Length)
            {
                throw new InvalidDataException("truncated");
            }
            if (rtype == (ushort)type)
            {
                if (type == RecordType.A && len == 4)
                {
                    var ip = new IPAddress(r.AsSpan(data, 4));
                    if (!IsBogus(ip))
                    {
                        result.Add(ip.ToString());
                    }
                }
                else if (type == RecordType.Txt)
                {
                    var sb = new System.Text.StringBuilder();
                    for (var p = data; p < data + len;)
                    {
                        var n = r[p];
                        sb.Append(System.Text.Encoding.UTF8.GetString(r, p + 1, Math.Min(n, data + len - p - 1)));
                        p += n + 1;
                    }
                    result.Add(sb.ToString());
                }
            }
            pos = data + len;
        }
        return result;
    }

    private static int SkipName(byte[] r, int pos)
    {
        while (pos < r.Length)
        {
            var len = r[pos];
            if (len == 0)
            {
                return pos + 1;
            }
            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2;
            }
            pos += len + 1;
        }
        throw new InvalidDataException("truncated name");
    }

    /// <summary>Addresses no public answer for a real host contains: stubs, private, the cores' fake-IP range.</summary>
    public static bool IsBogus(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] is 0 or 10 or 127
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 198 && b[1] is 18 or 19);
    }
}
