using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ServiceLib.Manager;

/// <summary>
/// AmneziaWG: WireGuard with obfuscation (junk packets, padded and relabelled handshakes) that Russian DPI
/// doesn't recognise. Upstream sing-box and Xray don't support it, so AWG servers run on the sing-box
/// fork shtorm-7/sing-box-extended (GPL-3.0): an "amnezia" object inside the wireguard endpoint.
/// The fork is downloaded on first use only, checked against a pinned SHA-256, kept in its own folder
/// and used only for configs that contain AWG settings; everything else stays on the regular cores.
/// </summary>
public static class AmneziaCore
{
    private const string _tag = "AmneziaCore";

    public const string Repo = "shtorm-7/sing-box-extended";
    public const string Version = "v1.14.3-extended-2.7.3";
    private const string WindowsAsset = "sing-box-1.14.3-extended-2.7.3-windows-amd64.zip";
    private const string WindowsSha256 = "ab7a7bddd51524854b853da1e72a8b94626d56aee0acfca46239146df6c099c7";

    private static readonly SemaphoreSlim _lock = new(1, 1);

    private enum Kind { Number, Text, Flag }

    /// <summary>
    /// .conf keys → the fork's JSON names (option/wireguard.go of the pinned version). Numbers: a plain number, or
    /// a range "100-200" (AWG 2.0 H1–H4, AWG 3.0 timers and padding) passed as a string. AWG 1.5's J1–J3 / Itime
    /// aren't in the fork: the core refuses unknown fields, so they are left out.
    /// </summary>
    private static readonly (string Conf, string Json, Kind Kind)[] Keys =
    [
        ("Jc", "jc", Kind.Number), ("Jmin", "jmin", Kind.Number), ("Jmax", "jmax", Kind.Number),
        ("S1", "s1", Kind.Number), ("S2", "s2", Kind.Number), ("S3", "s3", Kind.Number), ("S4", "s4", Kind.Number),
        ("H1", "h1", Kind.Number), ("H2", "h2", Kind.Number), ("H3", "h3", Kind.Number), ("H4", "h4", Kind.Number),
        ("I1", "i1", Kind.Text), ("I2", "i2", Kind.Text), ("I3", "i3", Kind.Text), ("I4", "i4", Kind.Text), ("I5", "i5", Kind.Text),
        // AWG 3.0
        ("HeaderProtectionKey", "header_protection_key", Kind.Text),
        ("ContentPaddingAddition", "content_padding_addition", Kind.Number),
        ("RekeyAfterTime", "rekey_after_time", Kind.Number), ("RekeyTimeout", "rekey_timeout", Kind.Number),
        ("RejectAfterTime", "reject_after_time", Kind.Number), ("KeepaliveTimeout", "keepalive_timeout", Kind.Number),
        ("MaxHandshakeAttempts", "max_handshake_attempts", Kind.Number),
        // AWG 3.1
        ("RandomTrailers", "random_trailers", Kind.Flag), ("DisableCookies", "disable_cookies", Kind.Flag),
    ];

    private static readonly HashSet<string> JsonNames = Keys.Select(k => k.Json).ToHashSet(StringComparer.Ordinal);

    /// <summary>.conf keys that make a WireGuard config an AmneziaWG one.</summary>
    public static IEnumerable<string> ConfKeys => Keys.Select(k => k.Conf);

    public static string Folder => Utils.GetBinPath("", "sing_box_amnezia");

    public static string ExePath => Path.Combine(Folder, Utils.GetExeName("sing-box"));

    public static bool IsAmnezia(ProfileItem item)
        => item.ConfigType == EConfigType.WireGuard && item.GetProtocolExtra().WgAmnezia.IsNotEmpty();

    /// <summary>AWG settings of a WireGuard [Interface] section as JSON, or null when there are none.</summary>
    public static string? FromConf(IReadOnlyDictionary<string, string> interfaceSection)
    {
        var obj = new JsonObject();
        var section = new Dictionary<string, string>(interfaceSection, StringComparer.OrdinalIgnoreCase);
        foreach (var (conf, json, kind) in Keys)
        {
            if (!section.TryGetValue(conf, out var value) || value.IsNullOrEmpty())
            {
                continue;
            }
            value = value.Trim();
            switch (kind)
            {
                case Kind.Flag:
                    if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase))
                    {
                        obj[json] = true;
                    }
                    break;
                case Kind.Number:
                    obj[json] = long.TryParse(value, out var n) ? JsonValue.Create(n) : JsonValue.Create(value);
                    break;
                default:
                    obj[json] = value;
                    break;
            }
        }
        return obj.Count == 0 ? null : obj.ToJsonString();
    }

    public static JsonObject? ToJson(string? stored)
    {
        if (stored.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(stored!) is not JsonObject obj)
            {
                return null;
            }
            // Profiles imported by older builds may hold AWG 1.5 keys (j1, itime) the fork refuses: drop them.
            foreach (var unknown in obj.Select(kv => kv.Key).Where(k => !JsonNames.Contains(k)).ToList())
            {
                obj.Remove(unknown);
            }
            return obj.Count == 0 ? null : obj;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>Whether a generated sing-box config has AWG settings (and so needs the fork).</summary>
    public static bool ConfigNeedsIt(string configPath)
    {
        try
        {
            var path = Path.IsPathRooted(configPath) ? configPath : Utils.GetBinConfigPath(configPath);
            return File.Exists(path) && File.ReadAllText(path).Contains("\"amnezia\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Path of the fork's executable, downloading it first if needed; null with a message on failure.</summary>
    public static async Task<(string? Path, string Message)> EnsureAsync(Func<string, Task>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(ExePath))
        {
            return (ExePath, string.Empty);
        }
        if (!Utils.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            return (null, AppText.AmneziaUnsupported);
        }

        await _lock.WaitAsync(ct);
        var zipPath = Utils.GetTempPath($"amnezia-core-{Utils.GetGuid()}.zip");
        var staging = Folder + ".staging";
        try
        {
            if (File.Exists(ExePath))
            {
                return (ExePath, string.Empty);
            }
            if (progress != null)
            {
                await progress(AppText.AmneziaDownloading);
            }
            using (var download = DownloadService.CreateDownloadServiceWithFallback())
            {
                string? error = null;
                download.Error += (_, e) => error ??= e.GetException().Message;
                var url = $"https://github.com/{Repo}/releases/download/{Version}/{WindowsAsset}";
                await download.DownloadFileAsync(new() { FileUrl = url, FilePath = zipPath }, ct);
                if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
                {
                    return (null, string.Format(AppText.AmneziaDownloadFailed, error ?? url));
                }
            }

            if (!HashMatches(zipPath, WindowsSha256))
            {
                Logging.SaveLog($"{_tag}: SHA-256 mismatch for {WindowsAsset}");
                return (null, AppText.AmneziaHashMismatch);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
            ZipFile.ExtractToDirectory(zipPath, staging);
            var exe = Directory.GetFiles(staging, Utils.GetExeName("sing-box"), SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null)
            {
                return (null, string.Format(AppText.AmneziaDownloadFailed, "sing-box.exe not in the archive"));
            }
            Directory.CreateDirectory(Folder);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(exe)!))
            {
                File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), true);
            }
            Logging.SaveLog($"{_tag}: installed {Version}");
            return (ExePath, string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.SaveLog(_tag, ex);
            return (null, string.Format(AppText.AmneziaDownloadFailed, ex.Message));
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            catch
            {
                // temp files only
            }
            _lock.Release();
        }
    }

    public static bool HashMatches(string path, string sha256Hex)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
