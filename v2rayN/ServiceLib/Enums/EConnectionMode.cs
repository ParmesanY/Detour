namespace ServiceLib.Enums;

/// <summary>How traffic of a connection rule target is sent.</summary>
public enum EConnectionMode
{
    /// <summary>Through the VPN server.</summary>
    Vpn = 0,

    /// <summary>Directly, with zapret (winws) handling DPI for the target's domains/IPs.</summary>
    Zapret = 1,

    /// <summary>zapret first; switched to VPN automatically when the target does not work directly.</summary>
    ZapretThenVpn = 2,

    /// <summary>Directly, no zapret.</summary>
    Direct = 3,

    /// <summary>Dropped.</summary>
    Block = 4,
}

/// <summary>What a connection rule target refers to.</summary>
public enum EConnectionTarget
{
    Domain = 0,
    Ip = 1,
    Process = 2,
    Preset = 3,
}
