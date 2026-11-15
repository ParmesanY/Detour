using System.Text.Json.Nodes;

namespace ServiceLib.Manager;

/// <summary>
/// Cloudflare WARP (experimental): registers a free WARP device and adds two servers.
/// 1. "WARP" — a regular WireGuard profile (connection rules apply as with any server).
/// 2. "WARP + noise" — an Xray config whose WireGuard sends random UDP noise first
///    (streamSettings.finalmask), which gets the handshake past DPI that drops plain WireGuard.
/// WARP hides traffic from the ISP but exits in a nearby Cloudflare location, so sites that geo-block
/// Russia may still do so. Keys are made by sing-box (generate wg-keypair).
/// </summary>
public static class WarpManager
{
    private const string _tag = "WARP";
    private const string RegUrl = "https://api.cloudflareclient.com/v0a2158/reg";

    /// <summary>Cloudflare anycast WARP endpoint by IP: the engage.* name is often blocked by DNS.</summary>
    public const string DefaultEndpoint = "162.159.192.1:2408";

    public record Registration(string PrivateKey, string PeerPublicKey, string AddressV4, string? AddressV6, string? ClientId);

    public static async Task<(int Added, string Message)> CreateProfilesAsync(Config config, string? subid)
    {
        try
        {
            var keys = await GenerateKeyPairAsync();
            if (keys is null)
            {
                return (0, "sing-box is needed to make WireGuard keys: install the cores first");
            }

            var body = new JsonObject
            {
                ["key"] = keys.Value.Public,
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
                return (0, $"Cloudflare answered {(int)resp.StatusCode}");
            }
            var reg = ParseRegistration(text, keys.Value.Private);
            if (reg is null)
            {
                return (0, "unexpected answer from Cloudflare");
            }

            subid ??= string.Empty;
            var added = 0;

            var wg = WireguardFmt.ResolveConfig(BuildWireGuardConf(reg))?.FirstOrDefault();
            if (wg != null)
            {
                wg.Remarks = "WARP";
                wg.Subid = subid;
                if (reg.ClientId.IsNotEmpty())
                {
                    wg.SetProtocolExtra(wg.GetProtocolExtra() with { WgReserved = reg.ClientId });
                }
                if (await ConfigHandler.AddWireguardServer(config, wg) == 0)
                {
                    added++;
                }
            }

            var json = BuildXrayNoiseConfig(reg, AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
            var copy = Utils.GetTempPath($"{Utils.GetGuid(false)}.json");
            await File.WriteAllTextAsync(copy, json);
            var custom = new ProfileItem
            {
                ConfigType = EConfigType.Custom,
                CoreType = ECoreType.Xray,
                Address = copy,
                Remarks = "WARP + noise",
                Subid = subid,
            };
            if (await ConfigHandler.AddCustomServer(config, custom, true) == 0)
            {
                added++;
            }
            await ConfigHandler.SaveConfig(config);
            return (added, $"WARP: {added}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return (0, ex.GetBaseException().Message);
        }
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

    public static string BuildWireGuardConf(Registration r, string endpoint = DefaultEndpoint)
    {
        var address = r.AddressV6.IsNullOrEmpty() ? $"{r.AddressV4}/32" : $"{r.AddressV4}/32, {r.AddressV6}/128";
        return $"""
            [Interface]
            PrivateKey = {r.PrivateKey}
            Address = {address}
            DNS = 1.1.1.1
            MTU = 1280

            [Peer]
            PublicKey = {r.PeerPublicKey}
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = {endpoint}
            """;
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

    public static string BuildXrayNoiseConfig(Registration r, int socksPort, string endpoint = DefaultEndpoint)
    {
        var addresses = new JsonArray($"{r.AddressV4}/32");
        if (r.AddressV6.IsNotEmpty())
        {
            addresses.Add((JsonNode)$"{r.AddressV6}/128");
        }
        var noise = new JsonArray(
            new JsonObject { ["rand"] = "50-100", ["delay"] = "1-5" },
            new JsonObject { ["rand"] = "50-100", ["delay"] = "1-5" });
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
                    },
                }),
            ["outbounds"] = new JsonArray(
                new JsonObject
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
                    ["streamSettings"] = new JsonObject
                    {
                        ["finalmask"] = new JsonObject
                        {
                            ["udp"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "noise",
                                ["settings"] = new JsonObject { ["noise"] = noise },
                            }),
                        },
                    },
                },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" }),
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
