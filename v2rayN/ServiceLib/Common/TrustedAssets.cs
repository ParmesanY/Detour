namespace ServiceLib.Common;

/// <summary>
/// Versions of the third-party programs Detour runs (Xray, sing-box, zapret, TG WS Proxy) that the maintainer
/// checked and signed with the release key: strategies/trusted-assets.json + .sig in the project repo, built by
/// tools/sign-trusted-assets.ps1 from tools/cores.lock.json. An in-app download is installed only when its URL
/// is on the list with the same SHA-256. GitHub's own digest can't do this: whoever takes over an upstream
/// account publishes a matching one. Fail closed: no valid list, or a version not on it, means no install
/// (the cores shipped inside a signed Detour release are covered by that release's signature). Builds without a
/// configured repo (local builds) fall back to GitHub's digest.
/// </summary>
public static class TrustedAssets
{
    private const string _tag = "TrustedAssets";
    public const string ListPath = "strategies/trusted-assets.json";
    private static Dictionary<string, string>? _cache;
    private static DateTime _cacheTime;

    /// <summary>URL → SHA-256 from the list's text; null when the text isn't a valid list.</summary>
    public static Dictionary<string, string>? Parse(string? json)
    {
        if (JsonUtils.ParseJson(json) is not JsonObject root || root["assets"] is not JsonObject assets)
        {
            return null;
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (url, value) in assets)
        {
            if (value is JsonValue v && v.TryGetValue(out string? hash) && hash is { Length: 64 } && hash.All(Uri.IsHexDigit))
            {
                result[url] = hash.ToLowerInvariant();
            }
        }
        return result;
    }

    /// <summary>The list when <paramref name="sigText"/> is the release key's signature of exactly these bytes.</summary>
    public static Dictionary<string, string>? Verify(string? json, string? sigText, string? publicKey = null)
    {
        publicKey ??= ReleaseSignature.PublicKey;
        var signature = ReleaseSignature.Parse(sigText);
        if (json is null || signature is null || publicKey.Length == 0)
        {
            return null;
        }
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ReleaseSignature.Verify(ms, signature, publicKey) ? Parse(json) : null;
    }

    public static string Url(string repoUrl, string tag, string name) => $"{repoUrl.TrimEnd('/')}/releases/download/{tag}/{name}";

    /// <summary>Null when the file may be installed, otherwise why not.</summary>
    public static string? Check(IReadOnlyDictionary<string, string>? list, string url, string file)
    {
        if (list is null)
        {
            return AppText.TrustedListUnavailable;
        }
        if (!list.TryGetValue(url, out var expected))
        {
            return string.Format(AppText.TrustedNotListed, url[(url.LastIndexOf("/download/", StringComparison.Ordinal) + 10)..]);
        }
        return GitHubDigest.Check(file, expected) is null ? null : AppText.TrustedMismatch;
    }

    /// <summary>The tag the list holds for a repo (the version to install when none was asked for), or null.</summary>
    public static string? TagFor(IReadOnlyDictionary<string, string>? list, string repoUrl, string assetSuffix)
    {
        var prefix = repoUrl.TrimEnd('/') + "/releases/download/";
        return list?.Keys
            .Where(u => u.StartsWith(prefix, StringComparison.Ordinal) && u.EndsWith(assetSuffix, StringComparison.Ordinal))
            .Select(u => u[prefix.Length..].Split('/')[0])
            .FirstOrDefault();
    }

    /// <summary>The signed list from the project repo (cached for an hour); null when unavailable or not signed right.</summary>
    public static async Task<Dictionary<string, string>?> LoadAsync(DownloadService downloadService, CancellationToken ct)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheTime < TimeSpan.FromHours(1))
        {
            return _cache;
        }
        if (AppUpdateManager.Repo is not { Length: > 0 } repo)
        {
            return null;
        }
        var baseUrl = $"https://raw.githubusercontent.com/{repo}/main/{ListPath}";
        var json = await downloadService.TryDownloadString(baseUrl, ct);
        var sig = await downloadService.TryDownloadString(baseUrl + ReleaseSignature.Suffix, ct);
        var list = Verify(json, sig);
        Logging.SaveLog($"{_tag}: {(list is null ? "no valid signed list" : $"{list.Count} trusted assets")}");
        if (list is not null)
        {
            _cache = list;
            _cacheTime = DateTime.UtcNow;
        }
        return list;
    }

    /// <summary>
    /// The check every in-app download of a program goes through. Configured builds: the signed list, fail closed.
    /// Local builds: GitHub's digest. Null = install.
    /// </summary>
    public static async Task<string?> CheckAsync(DownloadService downloadService, string repoUrl, string tag, string name, string file, CancellationToken ct)
    {
        if (!AppUpdateManager.IsConfigured)
        {
            return await GitHubDigest.VerifyAsync(downloadService, repoUrl, tag, name, file, ct);
        }
        var url = Url(repoUrl, tag, name);
        var error = Check(await LoadAsync(downloadService, ct), url, file);
        Logging.SaveLog($"{_tag}: {url}: {error ?? "trusted"}");
        return error;
    }
}
