namespace ServiceLib.Common;

/// <summary>
/// VPN fragmentation strategies for custom (full JSON) Xray configs, which the client otherwise runs as-is:
/// adds Xray's fragment mask to every TLS/REALITY proxy outbound, and builds a test copy with a single
/// local SOCKS inbound for the strategy picker.
/// </summary>
public static class CustomConfigFragment
{
    private static readonly JsonDocumentOptions _readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly string[] _notProxies = ["freedom", "blackhole", "dns", "loopback"];

    private static JsonObject? Parse(string? json)
    {
        if (json.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json!, documentOptions: _readOptions) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A sing-box config has no "inbounds"/"outbounds" with Xray "protocol" fields.</summary>
    public static bool IsXray(string? json)
        => Parse(json)?["outbounds"] is JsonArray outbounds && outbounds.OfType<JsonObject>().Any(o => o["protocol"] != null);

    private static IEnumerable<JsonObject> ProxyOutbounds(JsonObject root)
        => (root["outbounds"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => o["protocol"]?.GetValue<string>() is { } p && !_notProxies.Contains(p, StringComparer.OrdinalIgnoreCase));

    /// <summary>Returns the config with the fragment mask added (unchanged when there is nothing to add or it isn't Xray JSON).</summary>
    public static string Apply(string json, VpnStrategy strategy)
    {
        var root = Parse(json);
        if (root == null)
        {
            return json;
        }
        var changed = false;
        foreach (var ob in ProxyOutbounds(root))
        {
            if (ob["streamSettings"] is not JsonObject stream
                || stream["security"]?.GetValue<string>() is not ("tls" or "reality")
                || stream["sockopt"]?["dialerProxy"] is JsonValue)
            {
                continue;
            }
            var mask = stream["finalmask"] as JsonObject ?? [];
            var tcp = mask["tcp"] as JsonArray ?? [];
            if (tcp.Count > 0)
            {
                continue; // the provider set its own masks: leave them
            }
            // Xray's "tlshello" fragment is incompatible with REALITY: cut the first writes instead.
            var packets = stream["security"]!.GetValue<string>() == "reality" && strategy.Packets == "tlshello" ? "1-3" : strategy.Packets;
            tcp.Add(new JsonObject
            {
                ["type"] = "fragment",
                ["settings"] = new JsonObject
                {
                    ["packets"] = packets,
                    ["lengths"] = new JsonArray(strategy.Lengths),
                    ["delays"] = new JsonArray(strategy.Delays),
                    ["maxSplit"] = int.TryParse(strategy.MaxSplit, out var ms) ? ms : 0,
                },
            });
            mask["tcp"] = tcp;
            stream["finalmask"] = mask;
            changed = true;
        }
        return changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : json;
    }

    /// <summary>First proxy server address and port, for the plain reachability check.</summary>
    public static (string Host, int Port)? FirstServer(string? json)
    {
        var root = Parse(json);
        if (root == null)
        {
            return null;
        }
        foreach (var ob in ProxyOutbounds(root))
        {
            var settings = ob["settings"] as JsonObject;
            var server = settings?["vnext"]?[0] ?? settings?["servers"]?[0] ?? settings;
            if (server?["address"] is JsonValue a && a.TryGetValue(out string? host) && host.IsNotEmpty()
                && server["port"] is JsonValue p && p.TryGetValue(out int port) && port > 0)
            {
                return (host!, port);
            }
        }
        return null;
    }

    /// <summary>
    /// A copy to run as a test core: the user's inbounds are replaced by one SOCKS inbound on the given
    /// port (keeping the first inbound's tag, so routing rules by inbound still match), logs are quiet.
    /// </summary>
    public static string? BuildTestConfig(string json, int socksPort, VpnStrategy? strategy)
    {
        var root = Parse(strategy == null ? json : Apply(json, strategy));
        if (root == null)
        {
            return null;
        }
        var tag = (root["inbounds"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(i => i["protocol"]?.GetValue<string>() is "socks" or "mixed" or "http")?["tag"]?.GetValue<string>() ?? "socks";
        root["inbounds"] = new JsonArray(new JsonObject
        {
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["port"] = socksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["udp"] = true },
        });
        root["log"] = new JsonObject { ["loglevel"] = "warning" };
        // These would listen on the same ports as the running core.
        root.Remove("metrics");
        (root["api"] as JsonObject)?.Remove("listen");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
