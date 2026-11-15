namespace ServiceLib.Common;

/// <summary>
/// Server domain names inside a custom (full JSON) Xray or sing-box config. The client resolves these
/// directly: under TUN the system resolver's queries for them would otherwise go to the remote DNS,
/// which is reached through the proxy, i.e. through the very core looking for its server.
/// </summary>
public static class CustomConfigDomains
{
    public static List<string> Extract(string? json)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json.IsNullOrEmpty())
        {
            return [];
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json!, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch
        {
            return [];
        }
        if (root?["outbounds"] is not JsonArray outbounds)
        {
            return [];
        }

        foreach (var ob in outbounds.OfType<JsonObject>())
        {
            // sing-box
            Add(ob["server"]);
            // Xray: vnext / servers / flat settings.address (newer format) / wireguard peers
            if (ob["settings"] is JsonObject settings)
            {
                Add(settings["address"]);
                foreach (var key in new[] { "vnext", "servers" })
                {
                    if (settings[key] is JsonArray list)
                    {
                        foreach (var item in list.OfType<JsonObject>())
                        {
                            Add(item["address"]);
                        }
                    }
                }
                if (settings["peers"] is JsonArray peers)
                {
                    foreach (var peer in peers.OfType<JsonObject>())
                    {
                        AddHostPort(peer["endpoint"]);
                    }
                }
            }
            // XHTTP split download via another address
            Add(ob["streamSettings"]?["xhttpSettings"]?["extra"]?["downloadSettings"]?["address"]);
        }
        return [.. result];

        void Add(JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue(out string? s) && Utils.IsDomain(s))
            {
                result.Add(s!.Trim());
            }
        }

        void AddHostPort(JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue(out string? s) && s.IsNotEmpty())
            {
                var host = s!.StartsWith('[') ? null : s.Split(':')[0];
                if (Utils.IsDomain(host))
                {
                    result.Add(host!);
                }
            }
        }
    }
}
