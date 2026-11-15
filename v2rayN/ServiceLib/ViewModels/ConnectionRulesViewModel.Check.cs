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

    private void InitCheck()
    {
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
