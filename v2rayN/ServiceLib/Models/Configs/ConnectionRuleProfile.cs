namespace ServiceLib.Models.Configs;

/// <summary>A saved set of connection rules the user can switch to (e.g. "Home", "Everything via VPN").</summary>
public class ConnectionRuleProfile
{
    public string Name { get; set; } = string.Empty;

    public List<ConnectionRule> Rules { get; set; } = [];
}
