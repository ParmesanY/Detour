namespace ServiceLib.Handler;

/// <summary>
/// Adds servers from files a VPN service lets you download (e.g. Proton VPN, own servers):
/// a WireGuard .conf, a full Xray / sing-box JSON config, or a text file with links.
/// The server is named after the file.
/// </summary>
public static class ServerFileImporter
{
    public enum EKind
    {
        Unknown,
        WireGuard,
        XrayJson,
        SingboxJson,
        OpenVpn,
        Links,
    }

    public record Result(int Added, string Message);

    /// <summary>What the file holds, judged by content (extensions lie, e.g. ".txt" with a WireGuard config).</summary>
    public static EKind Detect(string fileName, string content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".ovpn" || content.Contains("\nremote ", StringComparison.Ordinal) && content.Contains("client", StringComparison.Ordinal)
            && content.Contains("<ca>", StringComparison.Ordinal))
        {
            return EKind.OpenVpn;
        }
        if (content.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            && content.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            return EKind.WireGuard;
        }
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var root = JsonNode.Parse(content, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (root?["outbounds"] is JsonArray outbounds)
                {
                    // sing-box outbounds have "type", Xray outbounds have "protocol".
                    return outbounds.OfType<JsonObject>().Any(o => o["type"] != null) ? EKind.SingboxJson : EKind.XrayJson;
                }
            }
            catch
            {
                // not JSON after all
            }
            return EKind.Unknown;
        }
        return trimmed.Contains("://", StringComparison.Ordinal) ? EKind.Links : EKind.Unknown;
    }

    /// <summary>AmneziaWG obfuscation keys: the cores speak plain WireGuard only.</summary>
    public static bool HasAmneziaFields(string content)
    {
        return Regex.IsMatch(content, @"^\s*(Jc|Jmin|Jmax|S1|S2|H1|H2|H3|H4|I1|HeaderProtectionKey)\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    public static async Task<Result> ImportAsync(Config config, string path, string? subid)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            return new(0, ex.Message);
        }
        var name = Path.GetFileNameWithoutExtension(path);
        subid ??= string.Empty;

        switch (Detect(path, content))
        {
            case EKind.OpenVpn:
                return new(0, string.Format(AppText.ImportOpenVpn, Path.GetFileName(path)));

            case EKind.WireGuard:
            {
                var items = WireguardFmt.ResolveConfig(content);
                if (items is not { Count: > 0 })
                {
                    return new(0, string.Format(AppText.ImportFailed, Path.GetFileName(path)));
                }
                var added = 0;
                for (var i = 0; i < items.Count; i++)
                {
                    items[i].Remarks = items.Count == 1 ? name : $"{name} #{i + 1}";
                    items[i].Subid = subid;
                    if (await ConfigHandler.AddWireguardServer(config, items[i]) == 0)
                    {
                        added++;
                    }
                }
                await ConfigHandler.SaveConfig(config);
                var msg = string.Format(AppText.ImportAdded, added, Path.GetFileName(path));
                return new(added, HasAmneziaFields(content) ? msg + " " + AppText.ImportAmneziaNote : msg);
            }

            case EKind.XrayJson:
            case EKind.SingboxJson:
            {
                // AddCustomServer moves the file into the config folder: give it a copy.
                var copy = Utils.GetTempPath($"{Utils.GetGuid(false)}.json");
                await File.WriteAllTextAsync(copy, content);
                var item = new ProfileItem
                {
                    ConfigType = EConfigType.Custom,
                    CoreType = Detect(path, content) == EKind.SingboxJson ? ECoreType.sing_box : ECoreType.Xray,
                    Address = copy,
                    Remarks = name,
                    Subid = subid,
                };
                var ok = await ConfigHandler.AddCustomServer(config, item, true) == 0;
                return ok
                    ? new(1, string.Format(AppText.ImportAdded, 1, Path.GetFileName(path)))
                    : new(0, string.Format(AppText.ImportFailed, Path.GetFileName(path)));
            }

            case EKind.Links:
            {
                var added = await ConfigHandler.AddBatchServers(config, content, subid, false);
                return added > 0
                    ? new(added, string.Format(AppText.ImportAdded, added, Path.GetFileName(path)))
                    : new(0, string.Format(AppText.ImportFailed, Path.GetFileName(path)));
            }

            default:
                return new(0, string.Format(AppText.ImportUnknown, Path.GetFileName(path)));
        }
    }
}
