namespace ServiceLib.Manager;

/// <summary>
/// Live switching for rules with a server list ("@youtube +vpn = A; B; C"). Each list is a sing-box
/// selector starting on A. Every 15 s the current server is probed through the core (Clash API delay
/// test); after two failures in a row the selector moves to the first server from the top of the list
/// that answers. A live server is never left for a "better" one: the order only matters when choosing
/// the next server after a death. A reload starts from the top again.
/// </summary>
public sealed class RuleServerFailover
{
    private static readonly Lazy<RuleServerFailover> _instance = new(() => new());
    public static RuleServerFailover Instance => _instance.Value;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public const int FailuresToSwitch = 2;
    private const int ProbeTimeoutMs = 5000;

    private CancellationTokenSource? _cts;

    /// <summary>The state of one list: members in order, the selected one and its failures in a row.</summary>
    public sealed class ListState(string selector, List<string> members)
    {
        public string Selector { get; } = selector;
        public List<string> Members { get; } = members;
        public int Current { get; set; }
        public int Failures { get; set; }
    }

    public void Start(IReadOnlyDictionary<string, List<string>> selectors)
    {
        Stop();
        var lists = selectors.Where(s => s.Value.Count > 1).Select(s => new ListState(s.Key, s.Value)).ToList();
        if (lists.Count == 0)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => RunAsync(lists, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private static async Task RunAsync(List<ListState> lists, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Interval, ct);
                foreach (var list in lists)
                {
                    await CheckAsync(list, ProbeAsync, SelectAsync, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RuleServerFailover", ex);
        }
    }

    /// <summary>One check of a list. Returns the member switched to, or null when nothing changed.</summary>
    public static async Task<string?> CheckAsync(ListState list, Func<string, Task<bool>> probe,
        Func<string, string, Task> select, CancellationToken ct = default)
    {
        if (await probe(list.Members[list.Current]))
        {
            list.Failures = 0;
            return null;
        }
        if (++list.Failures < FailuresToSwitch)
        {
            return null;
        }

        for (var i = 0; i < list.Members.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i == list.Current || !await probe(list.Members[i]))
            {
                continue;
            }
            Logging.SaveLog($"RuleServerFailover: {list.Selector}: {list.Members[list.Current]} is down -> {list.Members[i]}");
            await select(list.Selector, list.Members[i]);
            list.Current = i;
            list.Failures = 0;
            return list.Members[i];
        }
        return null; // every server is down: stay and keep checking
    }

    private static async Task<bool> ProbeAsync(string outbound)
        => await ClashApiManager.Instance.ProbeDelay(outbound, ProbeTimeoutMs) > 0;

    private static Task SelectAsync(string selector, string outbound)
        => ClashApiManager.Instance.SetActiveProxy(selector, outbound);
}
