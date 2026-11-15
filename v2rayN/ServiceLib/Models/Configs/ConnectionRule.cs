namespace ServiceLib.Models.Configs;

/// <summary>
/// One line of the user's connection rules: a target (app, domain, IP/subnet or preset)
/// and how its traffic goes (VPN, zapret, zapret then VPN, direct, block).
/// </summary>
public class ConnectionRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>As typed: "Discord.exe", "discord.com", "1.2.3.0/24", "geosite:youtube", "@discord".</summary>
    public string Target { get; set; } = string.Empty;

    public EConnectionMode Mode { get; set; } = EConnectionMode.Vpn;

    public bool Enabled { get; set; } = true;

    public string? Remarks { get; set; }

    /// <summary>
    /// For <see cref="EConnectionMode.ZapretThenVpn" />: set when zapret did not work and the
    /// target was moved to VPN. Kept in the config so the decision survives a restart.
    /// </summary>
    public bool FallbackActive { get; set; }

    /// <summary>When the fallback was activated, used to retry zapret later.</summary>
    public DateTime? FallbackSince { get; set; }

    /// <summary>How many times in a row zapret failed after a retry; grows the retry interval.</summary>
    public int FallbackCount { get; set; }

    /// <summary>Domains seen for a process target in learning mode (fed to zapret and routing).</summary>
    public List<string>? LearnedDomains { get; set; }

    /// <summary>Public IPs a process target connected to without a domain, seen in learning mode.</summary>
    public List<string>? LearnedIps { get; set; }
}
