namespace ServiceLib.ViewModels;

/// <summary>One preset card on the rules page.</summary>
public record PresetCardModel(string Target, string Name, string Mode, string Why, string Line);

public partial class ConnectionRulesViewModel : MyReactiveObject
{
    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RevertCmd { get; }
    public ReactiveCommand<string, RxVoid> AddPresetCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LearnCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> SaveProfileCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoadProfileCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> DeleteProfileCmd { get; }

    public List<PresetCardModel> Presets { get; }


    public BulkObservableCollection<string> ProfileNames { get; } = [];

    [Reactive] public partial string? SelectedProfile { get; set; }

    [Reactive] public partial string NewProfileName { get; set; } = string.Empty;

    /// <summary>"" = as in rules, otherwise an <see cref="EConnectionMode" /> name.</summary>
    public List<KeyValuePair<string, string>> OverrideModes { get; } =
    [
        new("", AppText.RulesOverrideNone),
        new(nameof(EConnectionMode.Vpn), AppText.RulesOverrideVpn),
        new(nameof(EConnectionMode.Zapret), AppText.RulesOverrideZapret),
        new(nameof(EConnectionMode.Direct), AppText.RulesOverrideDirect),
    ];

    [Reactive] public partial string OverrideMode { get; set; } = string.Empty;

    /// <summary>Rules as editable text, one per line.</summary>
    [Reactive] public partial string RulesText { get; set; } = string.Empty;

    [Reactive] public partial string StatusText { get; set; } = string.Empty;

    [Reactive] public partial bool HasErrors { get; set; }

    [Reactive] public partial bool IsDirty { get; set; }

    [Reactive] public partial bool IsLearning { get; set; }

    /// <summary>Rules currently switched from zapret to VPN by the watchdog.</summary>
    [Reactive] public partial string FallbackText { get; set; } = string.Empty;

    private string _savedText = string.Empty;

    public ConnectionRulesViewModel()
    {
        _config = AppManager.Instance.Config;

        Presets = ConnectionPresets.AllWithComposite
            .Select(p => new PresetCardModel(p.Target, p.Name, AppText.ModeName(p.RecommendedMode), p.Why,
                ConnectionRuleParser.Format(new ConnectionRule { Target = p.Target, Mode = p.RecommendedMode })))
            .ToList();

        SaveCmd = ReactiveCommand.CreateFromTask(SaveAsync);
        RevertCmd = ReactiveCommand.Create(Load);
        AddPresetCmd = ReactiveCommand.Create<string>(AddPreset);
        LearnCmd = ReactiveCommand.CreateFromTask(LearnAsync, this.WhenAnyValue(x => x.IsLearning).Select(l => !l));

        this.WhenAnyValue(x => x.RulesText).Subscribe(t => IsDirty = t != _savedText);

        var hasSelected = this.WhenAnyValue(x => x.SelectedProfile).Select(p => p.IsNotEmpty());
        SaveProfileCmd = ReactiveCommand.CreateFromTask(SaveProfileAsync,
            this.WhenAnyValue(x => x.NewProfileName).Select(n => n.Trim().Length > 0));
        LoadProfileCmd = ReactiveCommand.CreateFromTask(LoadProfileAsync, hasSelected);
        DeleteProfileCmd = ReactiveCommand.CreateFromTask(DeleteProfileAsync, hasSelected);

        OverrideMode = _config.ConnectionRulesOverride?.ToString() ?? string.Empty;
        this.WhenAnyValue(x => x.OverrideMode).Skip(1).SubscribeAsync(async m =>
        {
            EConnectionMode? mode = Enum.TryParse<EConnectionMode>(m, out var parsed) ? parsed : null;
            if (mode == _config.ConnectionRulesOverride)
            {
                return;
            }
            _config.ConnectionRulesOverride = mode;
            await ConfigHandler.SaveConfig(_config);
            AppEvents.CoreReloadRequested.Publish();
        });

        InitCheck();
        RefreshProfiles();
        Load();
        // A rule Detour added by itself: show it, unless the user is in the middle of an edit.
        SiteAutoFixManager.Instance.RulesChanged += () => RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (!IsDirty)
            {
                Load();
            }
        });
    }

    private void RefreshProfiles()
    {
        ProfileNames.Clear();
        ProfileNames.AddRange((_config.ConnectionRuleProfiles ?? []).Select(p => p.Name));
    }

    private async Task SaveProfileAsync()
    {
        // Save what is in the editor, so unsaved edits are not lost into the old rules.
        if (IsDirty)
        {
            await SaveAsync();
            if (HasErrors)
            {
                return;
            }
        }
        var name = NewProfileName.Trim();
        ConnectionRulesManager.SaveProfile(_config, name);
        await ConfigHandler.SaveConfig(_config);
        RefreshProfiles();
        SelectedProfile = name;
        NewProfileName = string.Empty;
        StatusText = string.Format(AppText.RulesProfileSaved, name);
    }

    private async Task LoadProfileAsync()
    {
        var name = SelectedProfile!;
        if (!ConnectionRulesManager.LoadProfile(_config, name))
        {
            return;
        }
        await ConfigHandler.SaveConfig(_config);
        Load();
        StatusText = string.Format(AppText.RulesProfileLoaded, name);
        AppEvents.CoreReloadRequested.Publish();
    }

    private async Task DeleteProfileAsync()
    {
        ConnectionRulesManager.DeleteProfile(_config, SelectedProfile!);
        await ConfigHandler.SaveConfig(_config);
        RefreshProfiles();
        SelectedProfile = null;
    }

    private void Load()
    {
        _savedText = ConnectionRulesManager.ToText(_config);
        RulesText = _savedText;
        HasErrors = false;
        StatusText = string.Empty;
        RefreshFallback();
    }

    private void RefreshFallback()
    {
        var active = (_config.ConnectionRules ?? []).Where(r => r.FallbackActive).Select(r => r.Target).ToList();
        FallbackText = active.Count == 0 ? string.Empty : $"{string.Join(", ", active)}: {AppText.RulesFallbackActive}";
    }

    private void AddPreset(string line)
    {
        var target = line.Split(' ')[0];
        if (RulesText.Split('\n').Any(l => l.Trim().StartsWith(target + " ", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        RulesText = RulesText.TrimEnd() + (RulesText.Trim().Length > 0 ? Environment.NewLine : "") + line;
    }

    private async Task SaveAsync()
    {
        var errors = ConnectionRulesManager.ReplaceFromText(_config, RulesText);
        if (errors.Count > 0)
        {
            HasErrors = true;
            StatusText = AppText.RulesErrors + Environment.NewLine + string.Join(Environment.NewLine, errors);
            return;
        }

        await ConfigHandler.SaveConfig(_config);
        _savedText = ConnectionRulesManager.ToText(_config);
        RulesText = _savedText;
        HasErrors = false;

        var warnings = ConnectionRuleCompiler.Compile(_config.ConnectionRules).Warnings;
        StatusText = warnings.Count == 0
            ? AppText.RulesSaved
            : AppText.RulesSaved + Environment.NewLine + string.Join(Environment.NewLine, warnings);
        RefreshFallback();

        // New routing and zapret lists take effect on core restart.
        AppEvents.CoreReloadRequested.Publish();
    }

    private async Task LearnAsync()
    {
        var processRules = (_config.ConnectionRules ?? [])
            .Where(r => r.Enabled && ConnectionRuleParser.GetTargetKind(r.Target) == EConnectionTarget.Process)
            .ToList();
        if (processRules.Count == 0)
        {
            StatusText = AppText.RulesLearnNoProcess;
            return;
        }

        IsLearning = true;
        try
        {
            var duration = TimeSpan.FromSeconds(60);
            var results = await Task.WhenAll(processRules.Select(async r =>
            {
                var (d, i) = await ConnectionLearningManager.Instance.LearnAsync(r, duration);
                return string.Format(AppText.RulesLearnDone, r.Target, r.LearnedDomains?.Count ?? 0, r.LearnedIps?.Count ?? 0);
            }));
            await ConfigHandler.SaveConfig(_config);
            StatusText = string.Join(Environment.NewLine, results);
            AppEvents.CoreReloadRequested.Publish();
        }
        finally
        {
            IsLearning = false;
        }
    }
}
