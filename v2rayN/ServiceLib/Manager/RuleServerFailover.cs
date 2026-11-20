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
    private List<ListState> _lists = [];

    /// <summary>Raised (any thread) when the lists change: start, stop or a switch.</summary>
    public event Action? StateChanged;

    /// <summary>The state of one list: members in order, the selected one and its failures in a row.</summary>
    public sealed class ListState(string selector, List<string> members, List<string>? names = null)
    {
        public string Selector { get; } = selector;
        public List<string> Members { get; } = members;
        public List<string> Names { get; } = names is { } n && n.Count == members.Count ? n : members;
        public int Current { get; set; }
        public int Failures { get; set; }
        /// <summary>The server the list moved away from at the last switch.</summary>
        public string? LastDown { get; set; }

        /// <summary>The list as written in the rule ("A; B; C").</summary>
        public string ListText => Selector.StartsWith(SelectorPrefix, StringComparison.Ordinal) ? Selector[SelectorPrefix.Length..] : Selector;
    }

    public const string SelectorPrefix = "servers: ";

    public record Status(string ListText, string Current, string? LastDown);

    public IReadOnlyList<Status> Snapshot()
        => _lists.Select(l => new Status(l.ListText, l.Names[l.Current], l.LastDown)).ToList();

    public void Start(IReadOnlyDictionary<string, List<string>> selectors, IReadOnlyDictionary<string, List<string>>? names = null)
    {
        Stop();
        var lists = selectors.Where(s => s.Value.Count > 1)
            .Select(s => new ListState(s.Key, s.Value, names?.GetValueOrDefault(s.Key)))
            .ToList();
        _lists = lists;
        StateChanged?.Invoke();
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
        if (_lists.Count > 0)
        {
            _lists = [];
            StateChanged?.Invoke();
        }
    }

    private async Task RunAsync(List<ListState> lists, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Interval, ct);
                foreach (var list in lists)
                {
                    var from = list.Names[list.Current];
                    if (await CheckAsync(list, ProbeAsync, SelectAsync, ct) != null)
                    {
                        NoticeManager.Instance.Enqueue(string.Format(AppText.RuleServerSwitched,
                            RuleTargets(list.ListText), from, list.Names[list.Current]));
                        StateChanged?.Invoke();
                    }
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

    /// <summary>The rules that use a server list ("@youtube, AyuGram.exe"), for messages.</summary>
    public static string RuleTargets(string listText)
    {
        var names = ServiceServerResolver.SplitList(listText);
        var targets = (AppManager.Instance.Config.ConnectionRules ?? [])
            .Where(r => r.Server != null && ServiceServerResolver.SplitList(r.Server).SequenceEqual(names, StringComparer.Ordinal))
            .Select(r => r.Target)
            .Distinct()
            .ToList();
        return targets.Count > 0 ? string.Join(", ", targets) : listText;
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
            Logging.SaveLog($"RuleServerFailover: {list.Selector}: {list.Names[list.Current]} is down -> {list.Names[i]}");
            await select(list.Selector, list.Members[i]);
            list.LastDown = list.Names[list.Current];
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
