using System.Security.Cryptography;

namespace ServiceLib.Common;

/// <summary>
/// GitHub computes a SHA-256 for every release asset on upload and publishes it in the API ("digest":
/// "sha256:…"). Checking a download against it catches a file swapped or damaged on the way (a mirror, a
/// proxy, a broken download) before Detour unpacks and runs it elevated. It doesn't protect against the
/// release owner's account being taken over; the API is often unreachable from Russia, so "unknown" lets
/// the install go on as before.
/// </summary>
public static class GitHubDigest
{
    private const string _tag = "GitHubDigest";

    /// <summary>"owner/name" from https://github.com/owner/name; null for anything else.</summary>
    public static string? RepoOf(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 2 ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>https://github.com/o/r/releases/download/tag/file.zip → (https://github.com/o/r, tag, file.zip); null otherwise.</summary>
    public static (string RepoUrl, string Tag, string Name)? ParseAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var p = uri.AbsolutePath.Trim('/').Split('/');
        return p.Length == 6 && p[2] == "releases" && p[3] == "download"
            ? ($"https://github.com/{p[0]}/{p[1]}", Uri.UnescapeDataString(p[4]), Uri.UnescapeDataString(p[5]))
            : null;
    }

    /// <summary>The lower-case hex SHA-256 of the named asset in a release API response, or null.</summary>
    public static string? Find(string? releaseJson, string assetName)
    {
        if (JsonUtils.ParseJson(releaseJson) is not JsonObject release || release["assets"] is not JsonArray assets)
        {
            return null;
        }
        foreach (var asset in assets.OfType<JsonObject>())
        {
            if (asset["name"] is JsonValue n && n.TryGetValue(out string? name) && name == assetName
                && asset["digest"] is JsonValue d && d.TryGetValue(out string? digest)
                && digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
                && digest[7..].All(Uri.IsHexDigit))
            {
                return digest[7..].ToLowerInvariant();
            }
        }
        return null;
    }

    public static string Sha256(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Null when the file matches or GitHub's hash is unknown; otherwise why the file is refused.</summary>
    public static string? Check(string file, string? expected)
    {
        if (expected is null)
        {
            return null;
        }
        var actual = Sha256(file);
        return actual == expected ? null : $"SHA-256 mismatch: expected {expected}, got {actual}";
    }

    /// <summary>Fetches the asset's hash from the GitHub API; null when GitHub is unreachable or has none.</summary>
    public static async Task<string?> GetAsync(DownloadService downloadService, string repoUrl, string tag, string assetName, CancellationToken ct)
    {
        var repo = RepoOf(repoUrl);
        if (repo is null)
        {
            return null;
        }
        try
        {
            var json = await downloadService.TryDownloadString($"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
            var digest = Find(json, assetName);
            Logging.SaveLog($"{_tag}: {repo} {tag} {assetName}: {digest ?? "no digest (GitHub API unreachable?)"}");
            return digest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Logging.SaveLog($"{_tag}: {repo} {tag}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the digest and checks the file; null = go on.</summary>
    public static async Task<string?> VerifyAsync(DownloadService downloadService, string repoUrl, string tag, string assetName, string file, CancellationToken ct) =>
        Check(file, await GetAsync(downloadService, repoUrl, tag, assetName, ct));
}
