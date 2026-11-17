using System.IO.Compression;
using System.Text.Json.Nodes;

namespace ServiceLib.Handler;

/// <summary>
/// One zip to send when asking for help: the diagnostics report, the app and core logs of the last days and
/// the generated core configs. Secrets are removed: share links, UUIDs, passwords and keys, subscription
/// tokens, the VPN servers' own addresses and the Windows user name. Sites the user visited stay in the
/// core logs (that is what shows what broke); the window says so before saving.
/// </summary>
public static partial class DiagnosticsExport
{
    private const int LogDays = 3;
    private const long MaxLogBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "uuid", "password", "pass", "passwd", "privateKey", "private_key", "publicKey", "public_key",
        "shortId", "short_id", "shortIds", "short_ids", "preSharedKey", "pre_shared_key", "psk", "auth", "auth_str",
        "token", "user", "username", "obfs-password", "obfs_password", "secret", "seed", "reserved", "spiderX",
    };

    private static readonly HashSet<string> ServerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "server", "serverName", "server_name", "sni", "host", "Host", "endpoint", "peer",
    };

    /// <summary>Writes the archive and returns its path.</summary>
    public static async Task<string> BuildAsync(Config config, string report, string targetPath, CancellationToken token = default)
    {
        var servers = await ServerAddressesAsync();
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        await using var fs = File.Create(targetPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        await AddTextAsync(zip, "report.txt", RedactText(report, servers), token);

        var logDir = Utils.GetLogPath();
        if (Directory.Exists(logDir))
        {
            var since = DateTime.Now.Date.AddDays(-(LogDays - 1));
            foreach (var file in new DirectoryInfo(logDir).GetFiles("*.txt").Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.LastWriteTime))
            {
                token.ThrowIfCancellationRequested();
                await AddTextAsync(zip, "logs/" + file.Name, RedactText(await ReadTailAsync(file.FullName, token), servers), token);
            }
        }

        var binConfigDir = Utils.GetBinConfigPath();
        if (Directory.Exists(binConfigDir))
        {
            foreach (var file in Directory.GetFiles(binConfigDir, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                var json = await File.ReadAllTextAsync(file, token);
                await AddTextAsync(zip, "configs/" + Path.GetFileName(file), RedactText(RedactJson(json), servers), token);
            }
        }
        return targetPath;
    }

    /// <summary>Hides secret and server fields of a core config; non-JSON text is returned as is.</summary>
    public static string RedactJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null)
            {
                return json;
            }
            Walk(node);
            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue && (SecretKeys.Contains(key) || ServerKeys.Contains(key)))
                    {
                        obj[key] = SecretKeys.Contains(key) ? "<secret>" : "<server>";
                    }
                    else if (value is JsonArray arr && (SecretKeys.Contains(key) || ServerKeys.Contains(key))
                             && arr.All(v => v is JsonValue))
                    {
                        obj[key] = new JsonArray(arr.Select(_ => (JsonNode?)JsonValue.Create(SecretKeys.Contains(key) ? "<secret>" : "<server>")).ToArray());
                    }
                    else if (value != null)
                    {
                        Walk(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item != null)
                    {
                        Walk(item);
                    }
                }
                break;
        }
    }

    /// <summary>Removes share links, UUIDs, subscription tokens, the given server addresses and the user name.</summary>
    public static string RedactText(string text, IEnumerable<string> serverAddresses)
    {
        if (text.IsNullOrEmpty())
        {
            return text;
        }
        var s = ShareLinkRegex().Replace(text, m => m.Groups[1].Value + "://<link>");
        s = UuidRegex().Replace(s, "<uuid>");
        s = TokenQueryRegex().Replace(s, m => m.Groups[1].Value + "=<secret>");
        s = SubPathRegex().Replace(s, "$1<secret>");
        foreach (var address in serverAddresses.Where(a => a.Length >= 4).OrderByDescending(a => a.Length))
        {
            s = s.Replace(address, "<server>", StringComparison.OrdinalIgnoreCase);
        }
        var user = Environment.UserName;
        if (user.Length > 2)
        {
            s = s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    private static async Task<List<string>> ServerAddressesAsync()
    {
        try
        {
            var items = await AppManager.Instance.ProfileItems("") ?? [];
            return items.Select(p => p.Address).Where(a => !a.IsNullOrEmpty()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("DiagnosticsExport", ex);
            return [];
        }
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken token)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxLogBytes)
        {
            fs.Seek(-MaxLogBytes, SeekOrigin.End);
        }
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync(token);
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken token)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), token);
    }

    [GeneratedRegex(@"\b(vless|vmess|trojan|ss|ssr|socks|hysteria2?|hy2|tuic|wireguard|wg|anytls|naive\+https|happ|incy|v2raytun)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ShareLinkRegex();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"\b(token|key|sub|uuid|password|pass|secret|auth)=[^\s&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenQueryRegex();

    /// <summary>Subscription URLs keep the token in the path: https://host/sub/AbCd… .</summary>
    [GeneratedRegex(@"(https?://[^\s/""']+/(?:sub|api/sub|s|link|subscribe)/)[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex SubPathRegex();
}
