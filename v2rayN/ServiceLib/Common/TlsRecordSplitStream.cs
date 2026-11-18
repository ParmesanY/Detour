namespace ServiceLib.Common;

/// <summary>
/// Cuts the first TLS record written through it (the ClientHello) into several TLS records and sends them in one
/// write — what the cores do for zapret rules in "TLS records" mode, so the strategy picker's probes see the same
/// handshake the browser traffic will. Everything else passes through unchanged.
/// </summary>
public sealed class TlsRecordSplitStream(Stream inner, string lengths, int maxSplit) : Stream
{
    private bool _done;

    /// <summary>The cut handshake: records with sizes from <paramref name="lengths" /> ("10-30"); with
    /// <paramref name="maxSplit" /> > 0, the last of that many records takes the rest. Null when it isn't one TLS
    /// handshake record.</summary>
    public static byte[]? Split(ReadOnlySpan<byte> data, string lengths, int maxSplit, Random? random = null)
    {
        if (data.Length < 6 || data[0] != 0x16 || data[1] != 0x03)
        {
            return null;
        }
        var recordLen = (data[3] << 8) | data[4];
        if (recordLen == 0 || data.Length < 5 + recordLen || !TryRange(lengths, out var min, out var max))
        {
            return null;
        }
        random ??= Random.Shared;
        var payload = data.Slice(5, recordLen);
        using var output = new MemoryStream(data.Length + 64);
        var pieces = 0;
        for (var from = 0; from < payload.Length;)
        {
            pieces++;
            var size = maxSplit > 0 && pieces >= maxSplit ? payload.Length - from : Math.Min(random.Next(min, max + 1), payload.Length - from);
            output.Write([data[0], data[1], data[2], (byte)(size >> 8), (byte)size]);
            output.Write(payload.Slice(from, size));
            from += size;
        }
        output.Write(data[(5 + recordLen)..]);
        return output.ToArray();
    }

    private static bool TryRange(string text, out int min, out int max)
    {
        var parts = text.Split('-');
        min = max = 0;
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out min) || !int.TryParse(parts[^1], out max))
        {
            return false;
        }
        return min >= 1 && max >= min;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_done)
        {
            _done = true;
            if (Split(buffer.Span, lengths, maxSplit) is { } cut)
            {
                await inner.WriteAsync(cut, cancellationToken);
                return;
            }
        }
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Flush() => inner.Flush();
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
