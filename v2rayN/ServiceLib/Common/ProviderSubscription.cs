namespace ServiceLib.Common;

/// <summary>
/// Compatibility with VPN providers' subscriptions made for other clients:
/// deep links (happ://add/…, incy://add|import/…, v2raytun://import/…), the "subscription-userinfo"
/// header (traffic and expiry) and the device headers some providers require (X-HWID, X-Device-*).
/// </summary>
public static class ProviderSubscription
{
    private static readonly string[] DeepLinkPrefixes =
    [
        "happ://add/", "incy://add/", "incy://import/", "v2raytun://import/", "v2raytun://add/",
        "hiddify://import/", "streisand://import/", "clash://install-config?url=", "sing-box://import-remote-profile?url=",
    ];

    /// <summary>The subscription URL inside a provider deep link, or null when the text is not one.</summary>
    public static string? ExtractSubscriptionUrl(string? text)
    {
        var s = text?.Trim();
        if (s.IsNullOrEmpty())
        {
            return null;
        }
        foreach (var prefix in DeepLinkPrefixes)
        {
            if (!s!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = s[prefix.Length..];
            // sing-box puts "#name" or "&name=" after the url; the url itself may be percent-encoded.
            var amp = rest.IndexOf("&name=", StringComparison.OrdinalIgnoreCase);
            if (amp > 0)
            {
                rest = rest[..amp];
            }
            if (!rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                rest = Uri.UnescapeDataString(rest);
            }
            return rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? rest
                : null;
        }
        return null;
    }

    public record UserInfo(long Upload, long Download, long Total, long Expire)
    {
        public long Used => Upload + Download;
    }

    /// <summary>Parses "upload=1; download=2; total=3; expire=1700000000" (any order, missing parts = 0).</summary>
    public static UserInfo? ParseUserInfo(string? header)
    {
        if (header.IsNullOrEmpty())
        {
            return null;
        }
        long upload = 0, download = 0, total = 0, expire = 0;
        var any = false;
        foreach (var part in header!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                continue;
            }
            var v = (long)d;
            switch (kv[0].ToLowerInvariant())
            {
                case "upload": upload = v; any = true; break;
                case "download": download = v; any = true; break;
                case "total": total = v; any = true; break;
                case "expire": expire = v; any = true; break;
            }
        }
        return any ? new UserInfo(upload, download, total, expire) : null;
    }

    /// <summary>
    /// Device headers in the form Happ/INCY send them. The id is a random per-install GUID (not the
    /// machine id): it only lets the provider count devices, and it is sent only when the user turned it on.
    /// </summary>
    public static Dictionary<string, string> DeviceHeaders(string installId)
    {
        var os = Environment.OSVersion.Version;
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["x-hwid"] = installId,
            ["x-device-os"] = Utils.IsWindows() ? "Windows" : Utils.IsMacOS() ? "macOS" : "Linux",
            ["x-ver-os"] = $"{os.Major}.{os.Minor}.{os.Build}",
            ["x-device-model"] = "PC",
        };
    }
}
