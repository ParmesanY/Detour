using System.Text.Json.Nodes;

namespace ServiceLib.Manager;

/// <summary>
/// Cloudflare WARP helpers: free device registration and the Xray config that the WARP page's local
/// proxy runs (WireGuard to Cloudflare, optionally with random UDP noise before the handshake via
/// streamSettings.finalmask, which gets past DPI that drops plain WireGuard). Keys are made by
/// sing-box (generate wg-keypair). WARP hides traffic from the ISP but exits in a nearby Cloudflare
/// location, so sites that geo-block Russia may still do so.
/// </summary>
public static class WarpManager
{
    private const string _tag = "WARP";
    private const string RegUrl = "https://api.cloudflareclient.com/v0a2158/reg";

    /// <summary>Cloudflare anycast WARP endpoint by IP: the engage.* name is often blocked by DNS.</summary>
    public const string DefaultEndpoint = "162.159.192.1:2408";

    /// <summary>Endpoints to offer when the default one is blocked.</summary>
    public static readonly string[] Endpoints =
    [
        "162.159.192.1:2408", "162.159.192.1:500", "162.159.192.1:1701", "162.159.192.1:4500",
        "162.159.195.1:2408", "188.114.97.1:2408", "188.114.98.1:2408",
    ];

    public record Registration(string PrivateKey, string PeerPublicKey, string AddressV4, string? AddressV6, string? ClientId);

    /// <summary>"ip:port" (IPv4 or [IPv6]) or "host:port".</summary>
    public static bool IsValidEndpoint(string? endpoint)
    {
        if (endpoint.IsNullOrEmpty())
        {
            return false;
        }
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(endpoint[(idx + 1)..], out var port) || port is < 1 or > 65535)
        {
            return false;
        }
        var host = endpoint[..idx].Trim('[', ']');
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>Any enabled rule sends traffic to WARP (directly or through the "all via …" switch).</summary>
    public static bool HasWarpRules(Config config) =>
        config.ConnectionRulesOverride == EConnectionMode.Warp
        || (config.ConnectionRulesOverride is null
            && (config.ConnectionRules ?? []).Any(r => r.Enabled && r.Mode == EConnectionMode.Warp));

    public static bool IsRegistered(WarpItem item) =>
        item.PrivateKey.IsNotEmpty() && item.PeerPublicKey.IsNotEmpty() && item.AddressV4.IsNotEmpty();

    public static Registration? FromItem(WarpItem item) => IsRegistered(item)
        ? new Registration(item.PrivateKey!, item.PeerPublicKey!, item.AddressV4!, item.AddressV6, item.ClientId)
        : null;

    /// <summary>Registers a new free WARP device and stores it in <see cref="WarpItem" />.</summary>
    public static async Task<(bool Success, string Message)> RegisterAsync(Config config)
    {
        try
        {
            var keys = await GenerateKeyPairAsync();
            if (keys is null)
            {
                return (false, AppText.WarpNoSingbox);
            }
            var reg = await RegisterDeviceAsync(keys.Value);
            if (reg is null)
            {
                return (false, AppText.WarpRegisterFailed);
            }
            var item = config.WarpItem;
            item.PrivateKey = reg.PrivateKey;
            item.PeerPublicKey = reg.PeerPublicKey;
            item.AddressV4 = reg.AddressV4;
            item.AddressV6 = reg.AddressV6;
            item.ClientId = reg.ClientId;
            await ConfigHandler.SaveConfig(config);
            return (true, AppText.WarpRegistered);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (false, ex.GetBaseException().Message);
        }
    }

    private static async Task<Registration?> RegisterDeviceAsync((string Private, string Public) keys)
    {
        var body = new JsonObject
        {
            ["key"] = keys.Public,
            ["install_id"] = "",
            ["fcm_token"] = "",
            ["tos"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["model"] = "PC",
            ["type"] = "Android",
            ["locale"] = "en_US",
        };
        using var handler = HttpClientHelper.Instance.CreateFallbackHandler(true);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Post, RegUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd("okhttp/3.12.1");
        req.Headers.TryAddWithoutValidation("CF-Client-Version", "a-6.30-3596");
        using var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Logging.SaveLog($"{_tag}: registration answered {(int)resp.StatusCode}");
            return null;
        }
        return ParseRegistration(text, keys.Private);
    }

    public static Registration? ParseRegistration(string json, string privateKey)
    {
        var root = JsonNode.Parse(json);
        var cfg = root?["config"];
        var peer = cfg?["peers"]?[0]?["public_key"]?.GetValue<string>();
        var v4 = cfg?["interface"]?["addresses"]?["v4"]?.GetValue<string>();
        if (peer.IsNullOrEmpty() || v4.IsNullOrEmpty())
        {
            return null;
        }
        return new Registration(privateKey, peer, v4,
            cfg?["interface"]?["addresses"]?["v6"]?.GetValue<string>(),
            cfg?["client_id"]?.GetValue<string>());
    }

    /// <summary>"base64 client_id" → its first 3 bytes, Xray's "reserved".</summary>
    public static int[] ReservedBytes(string? clientId)
    {
        try
        {
            var bytes = Convert.FromBase64String(clientId ?? "");
            return bytes.Length >= 3 ? [bytes[0], bytes[1], bytes[2]] : [0, 0, 0];
        }
        catch
        {
            return [0, 0, 0];
        }
    }

    /// <summary>Xray config of the WARP local proxy: SOCKS on 127.0.0.1:port → WireGuard to Cloudflare.</summary>
    public static string BuildXrayConfig(Registration r, int socksPort, string endpoint = DefaultEndpoint, bool noise = true)
    {
        var addresses = new JsonArray($"{r.AddressV4}/32");
        if (r.AddressV6.IsNotEmpty())
        {
            addresses.Add((JsonNode)$"{r.AddressV6}/128");
        }
        var warp = new JsonObject
        {
            ["tag"] = "warp",
            ["protocol"] = "wireguard",
            ["settings"] = new JsonObject
            {
                ["secretKey"] = r.PrivateKey,
                ["address"] = addresses,
                ["peers"] = new JsonArray(new JsonObject
                {
                    ["publicKey"] = r.PeerPublicKey,
                    ["endpoint"] = endpoint,
                    ["allowedIPs"] = new JsonArray("0.0.0.0/0", "::/0"),
                }),
                ["reserved"] = new JsonArray(ReservedBytes(r.ClientId).Select(b => (JsonNode)b).ToArray()),
                ["mtu"] = 1280,
                ["domainStrategy"] = "ForceIPv4",
            },
        };
        if (noise)
        {
            warp["streamSettings"] = new JsonObject
            {
                ["finalmask"] = new JsonObject
                {
                    ["udp"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "noise",
                        ["settings"] = new JsonObject
                        {
                            ["noise"] = new JsonArray(
                                new JsonObject { ["rand"] = "50-100", ["delay"] = "1-5" },
                                new JsonObject { ["rand"] = "50-100", ["delay"] = "1-5" }),
                        },
                    }),
                },
            };
        }
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray("1.1.1.1", "8.8.8.8") },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "socks",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls", "quic"),
                        ["routeOnly"] = true,
                    },
                }),
            ["outbounds"] = new JsonArray(warp),
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<(string Private, string Public)?> GenerateKeyPairAsync()
    {
        var exe = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box), out _);
        if (exe.IsNullOrEmpty() || !File.Exists(exe))
        {
            return null;
        }
        var psi = new ProcessStartInfo(exe, "generate wg-keypair")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            return null;
        }
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return ParseKeyPair(output);
    }

    public static (string Private, string Public)? ParseKeyPair(string output)
    {
        string? priv = null, pub = null;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }
            var key = parts[0].Trim();
            if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
            {
                priv = parts[1].Trim();
            }
            else if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
            {
                pub = parts[1].Trim();
            }
        }
        return priv.IsNullOrEmpty() || pub.IsNullOrEmpty() ? null : (priv, pub);
    }
}
