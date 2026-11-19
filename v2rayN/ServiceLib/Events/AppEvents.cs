namespace ServiceLib.Events;

public static class AppEvents
{
    public static readonly EventChannel<RxVoid> AddServerViaClipboardRequested = new();
    public static readonly EventChannel<bool> HasUpdateNotified = new();

    public static readonly EventChannel<ServerSpeedItem> DispatcherStatisticsRequested = new();

    public static readonly EventChannel<string> SendSnackMsgRequested = new();
    public static readonly EventChannel<string> SendMsgViewRequested = new();

    public static readonly EventChannel<RxVoid> AppExitRequested = new();
    public static readonly EventChannel<bool> ShutdownRequested = new();

    public static readonly EventChannel<ESysProxyType> SysProxyChangeRequested = new();

    /// <summary>Regenerate core configs and restart cores (e.g. after a zapret→VPN switch).</summary>
    public static readonly EventChannel<RxVoid> CoreReloadRequested = new();

    /// <summary>Files Detour had (WinDivert, winws, cores) are gone: most likely the antivirus took them.</summary>
    public static readonly EventChannel<List<string>> FilesRemovedDetected = new();
}
