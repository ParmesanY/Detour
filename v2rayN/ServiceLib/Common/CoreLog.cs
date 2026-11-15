namespace ServiceLib.Common;

/// <summary>
/// Copy of the cores' output and service messages (what the Log page shows) in
/// guiLogs\core-yyyy-MM-dd.txt, so a failed connection can be looked at after the app is closed.
/// </summary>
public static class CoreLog
{
    private static readonly Lock _gate = new();
    private const long MaxBytes = 20L * 1024 * 1024;

    public static string FilePath => Path.Combine(Utils.GetLogPath(), $"core-{DateTime.Now:yyyy-MM-dd}.txt");

    public static void Append(string? message)
    {
        if (message.IsNullOrEmpty())
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                var path = FilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    return;
                }
                var line = message!.EndsWith('\n') ? message : message + Environment.NewLine;
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        }
        catch
        {
            // logging must never break the core
        }
    }
}
