namespace ServiceLib.Models.Configs;

/// <summary>
/// Cloudflare WARP module: a free WARP device registration and the local proxy that serves it
/// (a separate Xray process on 127.0.0.1:Port). Connection rules with "+warp" go through it.
/// </summary>
public class WarpItem
{
    /// <summary>Start the WARP proxy together with the application.</summary>
    public bool Enabled { get; set; }

    /// <summary>Local SOCKS port on 127.0.0.1.</summary>
    public int Port { get; set; } = WarpProxyManager.DefaultPort;

    /// <summary>Send random UDP noise before the WireGuard handshake (gets past DPI that drops plain WireGuard).</summary>
    public bool Noise { get; set; } = true;

    /// <summary>Cloudflare endpoint, ip:port.</summary>
    public string Endpoint { get; set; } = WarpManager.DefaultEndpoint;

    // Device registration (empty until "Register").
    public string? PrivateKey { get; set; }
    public string? PeerPublicKey { get; set; }
    public string? AddressV4 { get; set; }
    public string? AddressV6 { get; set; }
    public string? ClientId { get; set; }
}
