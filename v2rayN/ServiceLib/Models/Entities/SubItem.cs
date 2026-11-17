namespace ServiceLib.Models.Entities;

public class SubItem
{
    [PrimaryKey]
    public string Id { get; set; }

    public string Remarks { get; set; }

    public string Url { get; set; }

    public string MoreUrl { get; set; }

    public bool Enabled { get; set; } = true;

    public string UserAgent { get; set; } = string.Empty;

    public string? RequestHeaders { get; set; }

    public int Sort { get; set; }

    public string? Filter { get; set; }

    public int AutoUpdateInterval { get; set; }

    public long UpdateTime { get; set; }

    public string? ConvertTarget { get; set; }

    public string? PrevProfile { get; set; }

    public string? NextProfile { get; set; }

    public int? PreSocksPort { get; set; }

    public string? Memo { get; set; }

    public ECoreType? CustomCoreType { get; set; }

    public string? PreResolver { get; set; }

    /// <summary>Send device headers (X-HWID, X-Device-*): only for providers that bind a subscription to devices.</summary>
    public bool SendHwid { get; set; }

    // From the provider's "subscription-userinfo" header at the last update (bytes; expire = unix seconds; 0 = unknown).
    public long InfoUsed { get; set; }

    public long InfoTotal { get; set; }

    public long InfoExpire { get; set; }
}
