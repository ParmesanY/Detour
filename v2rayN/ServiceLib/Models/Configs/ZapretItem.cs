namespace ServiceLib.Models.Configs;

/// <summary>
/// Settings of the built-in zapret (Flowseal zapret-discord-youtube build) integration.
/// </summary>
public class ZapretItem
{
    /// <summary>Start winws together with the application.</summary>
    public bool Enabled { get; set; }

    /// <summary>Strategy file name from the release, e.g. "general.bat" or "general (ALT).bat".</summary>
    public string Strategy { get; set; } = ZapretStrategyParser.DefaultStrategy;

    /// <summary>Game filter mode, same values as Flowseal: disabled, all, tcp, udp.</summary>
    public string GameFilterMode { get; set; } = ZapretStrategyParser.GameFilterDisabled;

    /// <summary>macOS/Linux: local SOCKS port of tpws on 127.0.0.1.</summary>
    public int SocksPort { get; set; } = ZapretManager.DefaultSocksPort;

    /// <summary>Fragmentation strategy for the VPN connection itself (VpnStrategyManager), "" = off.</summary>
    public string VpnStrategy { get; set; } = string.Empty;

    /// <summary>Installed release tag, empty if zapret is not installed.</summary>
    public string? InstalledVersion { get; set; }
}
