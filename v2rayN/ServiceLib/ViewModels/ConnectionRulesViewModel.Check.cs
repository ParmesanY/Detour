namespace ServiceLib.ViewModels;

/// <summary>One row of the block check: service, what works, the suggestion.</summary>
public record BlockCheckRow(string Name, string Paths, string Verdict, string Suggestion, bool Changed);

/// <summary>Rules page: block check and domain list updates.</summary>
public partial class ConnectionRulesViewModel
{
    public ReactiveCommand<RxVoid, RxVoid> CheckBlocksCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ApplyCheckCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateListsCmd { get; private set; } = null!;

    public BulkObservableCollection<BlockCheckRow> CheckRows { get; } = [];

    [Reactive] public partial bool IsChecking { get; set; }
    [Reactive] public partial bool HasCheckResults { get; set; }
    [Reactive] public partial string CheckStatus { get; set; } = string.Empty;
    [Reactive] public partial bool IsUpdatingLists { get; set; }
    [Reactive] public partial string ListsText { get; set; } = string.Empty;

    private List<BlockCheckResult> _checkResults = [];

    // "A site doesn't open": one address.
    public ReactiveCommand<RxVoid, RxVoid> SiteCheckCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> SiteApplyCmd { get; private set; } = null!;
    [Reactive] public partial string SiteInput { get; set; } = string.Empty;
    [Reactive] public partial string SiteResult { get; set; } = string.Empty;
    [Reactive] public partial bool IsSiteChecking { get; set; }
    [Reactive] public partial bool HasSiteSuggestion { get; set; }
    [Reactive] public partial string SiteApplyText { get; set; } = string.Empty;
    private SiteCheckResult? _siteResult;

    private void InitSiteCheck()
    {
        SiteCheckCmd = ReactiveCommand.CreateFromTask(CheckSiteAsync,
            this.WhenAnyValue(x => x.IsSiteChecking, x => x.SiteInput, (c, s) => !c && s.Trim().Length > 0));
        SiteApplyCmd = ReactiveCommand.CreateFromTask(ApplySiteAsync,
            this.WhenAnyValue(x => x.HasSiteSuggestion, x => x.IsSiteChecking, (h, c) => h && !c));
    }

    private async Task CheckSiteAsync()
    {
        HasSiteSuggestion = false;
        _siteResult = null;
        var host = SiteCheckManager.NormalizeHost(SiteInput);
        if (host is null)
        {
            SiteResult = AppText.SiteBadInput;
            return;
        }
        IsSiteChecking = true;
        try
        {
            var progress = new Progress<string>(s => SiteResult = s);
            var r = await Task.Run(() => SiteCheckManager.CheckAsync(_config, host, progress));
            _siteResult = r;
            static string State(bool ok) => ok ? AppText.CheckYes : AppText.CheckNo;
            var paths = new List<string>();
            if (r.Direct is { } d)
            {
                paths.Add($"{AppText.CheckDirect}: {State(d)}");
            }
            if (r.Zapret is { } z)
            {
                paths.Add($"{AppText.CheckZapret}: {State(z)}");
            }
            if (r.Vpn is { } v)
            {
                paths.Add($"VPN: {State(v)}");
            }
            var current = r.Current is { } c ? AppText.ModeName(c) : AppText.CheckNoRule;
            var suggestion = r.Suggested is { } s
                ? string.Format(AppText.CheckSuggest, AppText.ModeName(s)) + $" ({string.Format(AppText.CheckCurrent, current)})"
                : string.Empty;
            SiteResult = string.Join(Environment.NewLine,
                new[] { r.Host, string.Join(" · ", paths), r.Verdict, suggestion }.Where(l => l.Length > 0));
            HasSiteSuggestion = r.ChangeSuggested;
            SiteApplyText = r.Suggested is { } m ? string.Format(AppText.SiteApply, AppText.ModeName(m)) : string.Empty;
        }
        finally
        {
            IsSiteChecking = false;
        }
    }

    /// <summary>Puts the rule for the site on top (before a preset that may also cover it) and saves.</summary>
    private async Task ApplySiteAsync()
    {
        if (_siteResult is not { Suggested: { } mode } r)
        {
            return;
        }
        var exists = RulesText.Replace("\r\n", "\n").Split('\n')
            .Any(l => l.Trim().StartsWith(r.Host + " ", StringComparison.OrdinalIgnoreCase));
        RulesText = exists
            ? BlockCheckManager.SetRuleLine(RulesText, r.Host, mode)
            : ConnectionRuleParser.Format(new ConnectionRule { Target = r.Host, Mode = mode })
              + (RulesText.Trim().Length > 0 ? Environment.NewLine + RulesText : string.Empty);
        await SaveAsync();
        HasSiteSuggestion = false;
    }

    private void InitCheck()
    {
        InitSiteCheck();
        CheckBlocksCmd = ReactiveCommand.CreateFromTask(CheckBlocksAsync, this.WhenAnyValue(x => x.IsChecking).Select(c => !c));
        ApplyCheckCmd = ReactiveCommand.Create(ApplyCheck, this.WhenAnyValue(x => x.HasCheckResults, x => x.IsChecking, (h, c) => h && !c));
        UpdateListsCmd = ReactiveCommand.CreateFromTask(UpdateListsAsync, this.WhenAnyValue(x => x.IsUpdatingLists).Select(u => !u));

        PresetListManager.Instance.StateChanged += (_, _) => RxSchedulers.MainThreadScheduler.Schedule(RefreshListsText);
        RefreshListsText();
    }

    private void RefreshListsText()
    {
        var lists = PresetListManager.Instance;
        ListsText = lists.LastUpdated is { } updated
            ? string.Format(AppText.ListsInfo, updated.ToString("dd.MM.yyyy HH:mm"), lists.DomainCount)
            : AppText.ListsNever;
    }

    private async Task UpdateListsAsync()
    {
        IsUpdatingLists = true;
        try
        {
            var error = await PresetListManager.Instance.UpdateAsync();
            RefreshListsText();
            StatusText = error is null ? AppText.ListsUpdated : string.Format(AppText.ListsFailed, error);
            AppEvents.CoreReloadRequested.Publish();
        }
        finally
        {
            IsUpdatingLists = false;
        }
    }

    private async Task CheckBlocksAsync()
    {
        IsChecking = true;
        HasCheckResults = false;
        CheckRows.Clear();
        CheckStatus = string.Format(AppText.CheckRunning, 0);
        try
        {
            var progress = new Progress<int>(p => CheckStatus = string.Format(AppText.CheckRunning, p));
            _checkResults = await Task.Run(() => BlockCheckManager.CheckAsync(_config, progress));
            CheckRows.AddRange(_checkResults
                .OrderByDescending(r => r.ChangeSuggested)
                .ThenBy(r => r.Preset.Name)
                .Select(ToRow));
            HasCheckResults = _checkResults.Count > 0;
            CheckStatus = string.Empty;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static BlockCheckRow ToRow(BlockCheckResult r)
    {
        static string State(bool ok) => ok ? AppText.CheckYes : AppText.CheckNo;
        var paths = new List<string>();
        if (r.Direct is { } d)
        {
            paths.Add($"{AppText.CheckDirect}: {State(d)}");
        }
        if (r.Zapret is { } z)
        {
            paths.Add($"{AppText.CheckZapret}: {State(z)}");
        }
        var current = r.Current is { } c ? AppText.ModeName(c) : AppText.CheckNoRule;
        var suggestion = string.Format(AppText.CheckSuggest, AppText.ModeName(r.Suggested)) +
                         $" ({string.Format(AppText.CheckCurrent, current)})";
        return new(r.Preset.Name, string.Join(" · ", paths), r.Verdict, suggestion, r.ChangeSuggested);
    }

    private void ApplyCheck()
    {
        if (!_checkResults.Any(r => r.ChangeSuggested))
        {
            StatusText = AppText.CheckNoChanges;
            return;
        }
        RulesText = BlockCheckManager.ApplyToText(RulesText, _checkResults);
        StatusText = AppText.CheckApplied;
    }
}
