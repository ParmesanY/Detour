namespace ServiceLib.Common;

/// <summary>
/// "Servers per service" for custom (full Xray JSON) profiles. The main custom config runs as is and the
/// sing-box TUN pre-core in front of it applies the connection rules. A rule whose server is another
/// custom profile gets its own local SOCKS port: the pre-core sends that rule there, and this class adds
/// to the main config a SOCKS inbound on that port, the other config's proxy outbound (with the outbounds
/// it chains through) and a routing rule tying the two together.
/// </summary>
public static class CustomConfigRuleServers
{
    private static readonly HashSet<string> NonProxyProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "freedom", "blackhole", "dns", "loopback",
    };

    public static string TagPrefix(int port) => $"detour-srv{port}-";

    /// <summary>
    /// Returns the main config with the rule servers added, or the input unchanged when nothing could be added.
    /// </summary>
    public static string Apply(string mainJson, IEnumerable<(int Port, string ServerJson)> servers)
    {
        var list = servers.ToList();
        if (list.Count == 0 || Parse(mainJson) is not JsonObject main)
        {
            return mainJson;
        }

        var inbounds = main["inbounds"] as JsonArray ?? [];
        var outbounds = main["outbounds"] as JsonArray ?? [];
        main["inbounds"] = inbounds;
        main["outbounds"] = outbounds;
        var routing = main["routing"] as JsonObject ?? new JsonObject();
        main["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? [];
        routing["rules"] = rules;

        var added = 0;
        foreach (var (port, serverJson) in list)
        {
            if (Parse(serverJson) is not JsonObject server
                || server["outbounds"] is not JsonArray serverOutbounds
                || ProxyOutbounds(serverOutbounds) is not { Count: > 0 } chain)
            {
                continue;
            }

            var prefix = TagPrefix(port);
            foreach (var ob in chain)
            {
                var copy = ob.DeepClone().AsObject();
                copy["tag"] = prefix + ob["tag"]?.ToString();
                if (copy["streamSettings"]?["sockopt"] is JsonObject sockopt && sockopt["dialerProxy"]?.ToString() is { Length: > 0 } dialer)
                {
                    sockopt["dialerProxy"] = prefix + dialer;
                }
                if (copy["proxySettings"] is JsonObject proxySettings && proxySettings["tag"]?.ToString() is { Length: > 0 } via)
                {
                    proxySettings["tag"] = prefix + via;
                }
                outbounds.Add(copy);
            }

            inbounds.Add(new JsonObject
            {
                ["tag"] = prefix + "in",
                ["listen"] = Global.Loopback,
                ["port"] = port,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
            });
            rules.Insert(added, new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(prefix + "in"),
                ["outboundTag"] = prefix + chain[0]["tag"]?.ToString(),
            });
            added++;
        }

        return added == 0 ? mainJson : JsonUtils.Serialize(main);
    }

    /// <summary>
    /// The proxy outbound of a config (tag "proxy" first, else the first proxy protocol) followed by the
    /// outbounds it dials through (sockopt.dialerProxy, proxySettings.tag). Every one of them has a tag.
    /// </summary>
    public static List<JsonObject> ProxyOutbounds(JsonArray outbounds)
    {
        var all = outbounds.OfType<JsonObject>()
            .Where(o => o["tag"]?.ToString() is { Length: > 0 })
            .ToList();
        var proxies = all.Where(o => o["protocol"]?.ToString() is { Length: > 0 } p && !NonProxyProtocols.Contains(p)).ToList();
        var first = proxies.FirstOrDefault(o => o["tag"]?.ToString() == Global.ProxyTag) ?? proxies.FirstOrDefault();
        if (first == null)
        {
            return [];
        }

        var result = new List<JsonObject> { first };
        var next = first;
        while (Via(next) is { } via
               && result.All(o => o["tag"]?.ToString() != via)
               && all.FirstOrDefault(o => o["tag"]?.ToString() == via) is { } hop)
        {
            result.Add(hop);
            next = hop;
        }
        return result;
    }

    private static string? Via(JsonObject outbound)
        => outbound["streamSettings"]?["sockopt"]?["dialerProxy"]?.ToString() is { Length: > 0 } d ? d
            : outbound["proxySettings"]?["tag"]?.ToString() is { Length: > 0 } t ? t
            : null;

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch
        {
            return null;
        }
    }
}
