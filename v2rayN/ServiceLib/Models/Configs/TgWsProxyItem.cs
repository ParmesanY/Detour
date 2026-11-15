namespace ServiceLib.Models.Configs;

/// <summary>
/// Settings of the built-in TG WS Proxy (Flowseal/tg-ws-proxy): a local MTProto proxy for Telegram
/// Desktop that tunnels Telegram traffic over WebSocket/TLS to Telegram's own domains, no VPN needed.
/// </summary>
public class TgWsProxyItem
{
    /// <summary>Start the proxy together with the application.</summary>
    public bool Enabled { get; set; }

    /// <summary>Local listen port on 127.0.0.1.</summary>
    public int Port { get; set; } = TgWsProxyManager.DefaultPort;

    /// <summary>MTProto secret, 32 hex characters. Generated on first start.</summary>
    public string? Secret { get; set; }

    /// <summary>Installed release tag, empty if not installed.</summary>
    public string? InstalledVersion { get; set; }
}
