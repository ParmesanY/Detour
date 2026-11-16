namespace ServiceLib.Models.Configs;

/// <summary>Detour Map: sending anonymous block measurements to the public map. Off until the user turns it on.</summary>
public class BlockMapItem
{
    /// <summary>Measure and send in the background every few hours (opt-in).</summary>
    public bool Enabled { get; set; }

    /// <summary>"home" or "mobile": the map shows them separately (mobile networks block differently).</summary>
    public string Net { get; set; } = "home";

    /// <summary>UTC time of the last measurement attempt, sent or not: the background schedule counts from it.</summary>
    public DateTime? LastAttempt { get; set; }

    /// <summary>UTC time the collector last accepted a report.</summary>
    public DateTime? LastSent { get; set; }
}
