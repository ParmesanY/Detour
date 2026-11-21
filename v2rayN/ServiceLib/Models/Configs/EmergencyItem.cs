namespace ServiceLib.Models.Configs;

/// <summary>Emergency mode: what normally goes through the VPN goes through Tor (TorManager).</summary>
[Serializable]
public class EmergencyItem
{
    /// <summary>On right now (kept over a restart: the servers are probably still blocked).</summary>
    public bool Active { get; set; }

    /// <summary>The user's own Tor bridges (WebTunnel / obfs4 lines from @GetBridgesBot); empty = Snowflake.</summary>
    public string Bridges { get; set; } = string.Empty;

    /// <summary>Offer the emergency mode when failover has run out of servers (null = on).</summary>
    public bool? Offer { get; set; }
}
