namespace ServiceLib.Manager;

/// <summary>
/// Closes the "local proxy without a password" hole (runetfreedom, 2026): any program on the PC could
/// use 127.0.0.1:10808 to detect the VPN and learn the server address. In TUN mode every app already
/// goes through the tunnel, so the local port gets random per-session credentials. With the system
/// proxy it stays open: Windows cannot pass proxy credentials for apps.
/// </summary>
public static class LocalProxyGuard
{
    private static readonly Lazy<(string User, string Pass)> _session = new(() =>
        (RandomToken(8), RandomToken(24)));

    public static (string User, string Pass) SessionCredentials => _session.Value;

    public static bool ShouldProtect(Config config, bool isTunEnabled) =>
        isTunEnabled && config.ProtectLocalProxy != false;

    private static string RandomToken(int length)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
