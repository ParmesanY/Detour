namespace ServiceLib.Models.Entities;

public class RulesItem
{
    public string Id { get; set; }
    public string? Type { get; set; }
    public string? Port { get; set; }
    public string? Network { get; set; }
    public List<string>? InboundTag { get; set; }
    public string? OutboundTag { get; set; }
    public List<string>? Ip { get; set; }
    public List<string>? Domain { get; set; }
    public List<string>? Protocol { get; set; }
    public List<string>? Process { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Remarks { get; set; }
    public ERuleType? RuleType { get; set; }

    /// <summary>
    /// Process and IP must both match (one core rule). Otherwise each of Domain, Ip and Process is a rule of
    /// its own (either matches). Only for rules the client adds itself; null in the user's routing.
    /// </summary>
    public bool? ProcessAndIp { get; set; }
}
