using MaterialDesignColors;
using MaterialDesignColors.ColorManipulation;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace v2rayN.ViewModels;

public partial class ThemeSettingViewModel : MyReactiveObject
{
    private readonly PaletteHelper _paletteHelper = new();

    public BulkObservableCollection<Swatch> Swatches { get; } = [];

    [Reactive]
    public partial Swatch SelectedSwatch { get; set; }

    [Reactive] public partial string CurrentTheme { get; set; }

    [Reactive] public partial int CurrentFontSize { get; set; }

    [Reactive] public partial string CurrentLanguage { get; set; }

    public List<string> FontFamilies { get; } = v2rayN.Converters.MaterialDesignFonts.InstalledFonts();

    [Reactive] public partial string CurrentFontFamily { get; set; }

    public ThemeSettingViewModel()
    {
        _config = AppManager.Instance.Config;

        RegisterSystemColorSet(_config, ModifyTheme);

        BindingUI();
        RestoreUI();
    }

    private void RestoreUI()
    {
        Application.Current.Resources["ContentControlThemeFontFamily"] = v2rayN.Converters.MaterialDesignFonts.MyFont;
        ModifyTheme();
        ModifyFontSize();
        if (!_config.UiItem.ColorPrimaryName.IsNullOrEmpty())
        {
            var swatch = new SwatchesProvider().Swatches.FirstOrDefault(t => t.Name == _config.UiItem.ColorPrimaryName);
            if (swatch?.ExemplarHue?.Color is not null)
            {
                ChangePrimaryColor(swatch.ExemplarHue.Color);
            }
        }
    }

    private void BindingUI()
    {
        Swatches.AddRange(new SwatchesProvider().Swatches);
        if (!_config.UiItem.ColorPrimaryName.IsNullOrEmpty())
        {
            SelectedSwatch = Swatches.FirstOrDefault(t => t.Name == _config.UiItem.ColorPrimaryName);
        }
        CurrentTheme = _config.UiItem.CurrentTheme;
        CurrentFontSize = _config.UiItem.CurrentFontSize;
        CurrentLanguage = _config.UiItem.CurrentLanguage;
        CurrentFontFamily = v2rayN.Converters.MaterialDesignFonts.MyFont.Source;

        this.WhenAnyValue(x => x.CurrentFontFamily)
            .Where(y => y.IsNotEmpty())
            .SubscribeAsync(async name =>
            {
                if (name == v2rayN.Converters.MaterialDesignFonts.MyFont.Source)
                {
                    return;
                }
                _config.UiItem.CurrentFontFamily = name;
                // Fluent controls pick it up right away; the rest after a restart.
                Application.Current.Resources["ContentControlThemeFontFamily"] = new System.Windows.Media.FontFamily(name);
                await ConfigHandler.SaveConfig(_config);
                NoticeManager.Instance.Enqueue(ResUI.NeedRebootTips);
            });

        this.WhenAnyValue(x => x.CurrentTheme)
            .Where(y => y != null && !y.IsNullOrEmpty())
            .SubscribeAsync(async _ =>
             {
                 if (_config.UiItem.CurrentTheme != CurrentTheme)
                 {
                     _config.UiItem.CurrentTheme = CurrentTheme;
                     ModifyTheme();
                     await ConfigHandler.SaveConfig(_config);
                 }
             });

        this.WhenAnyValue(x => x.SelectedSwatch)
             .Where(y => y != null && !y.Name.IsNullOrEmpty())
             .SubscribeAsync(async _ =>
             {
                 if (SelectedSwatch == null
                 || SelectedSwatch.Name.IsNullOrEmpty()
                 || SelectedSwatch.ExemplarHue == null
                 || SelectedSwatch.ExemplarHue?.Color == null)
                 {
                     return;
                 }
                 if (_config.UiItem.ColorPrimaryName != SelectedSwatch?.Name)
                 {
                     _config.UiItem.ColorPrimaryName = SelectedSwatch?.Name;
                     ChangePrimaryColor(SelectedSwatch.ExemplarHue.Color);
                     await ConfigHandler.SaveConfig(_config);
                 }
             });

        this.WhenAnyValue(x => x.CurrentFontSize)
              .Where(y => y > 0)
              .SubscribeAsync(async _ =>
              {
                  if (_config.UiItem.CurrentFontSize != CurrentFontSize)
                  {
                      _config.UiItem.CurrentFontSize = CurrentFontSize;
                      ModifyFontSize();
                      await ConfigHandler.SaveConfig(_config);
                  }
              });

        this.WhenAnyValue(x => x.CurrentLanguage)
            .Where(y => y != null && !y.IsNullOrEmpty())
            .SubscribeAsync(async _ =>
            {
                if (CurrentLanguage.IsNotEmpty() && _config.UiItem.CurrentLanguage != CurrentLanguage)
                {
                    _config.UiItem.CurrentLanguage = CurrentLanguage;
                    Thread.CurrentThread.CurrentUICulture = new(CurrentLanguage);
                    await ConfigHandler.SaveConfig(_config);
                    NoticeManager.Instance.Enqueue(ResUI.NeedRebootTips);
                }
            });
    }

    public void ModifyTheme()
    {
        var baseTheme = CurrentTheme switch
        {
            nameof(ETheme.Dark) => BaseTheme.Dark,
            nameof(ETheme.Light) => BaseTheme.Light,
            _ => BaseTheme.Inherit,
        };

        var theme = _paletteHelper.GetTheme();
        theme.SetBaseTheme(baseTheme);
        _paletteHelper.SetTheme(theme);

        // Fluent (WPF-UI) resources follow the same setting.
        if (baseTheme == BaseTheme.Inherit)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme(true);
        }
        else
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                baseTheme == BaseTheme.Dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.None, true);
        }

        ApplyGraphitePalette(Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.Light);

        WindowsUtils.SetDarkBorder(Application.Current.MainWindow, CurrentTheme);
    }

    /// <summary>
    /// Puts the Detour palette (Themes/Graphite.*.xaml) last in the merged dictionaries so it wins over
    /// WPF-UI's theme, and sets the brand accent instead of the Windows accent color.
    /// </summary>
    private void ApplyGraphitePalette(bool dark)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Graphite.", StringComparison.OrdinalIgnoreCase) == true);
        if (old != null)
        {
            merged.Remove(old);
        }
        var palette = new ResourceDictionary { Source = new Uri($"/Themes/Graphite.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        merged.Add(palette);

        if (palette["DetourAccentColor"] is System.Windows.Media.Color accent)
        {
            // Exact shades: the single-color overload lightens the accent a lot for dark themes.
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(accent, accent, Shade(accent, 0.12), Shade(accent, 0.24));
            if (_config.UiItem.ColorPrimaryName.IsNullOrEmpty())
            {
                ChangePrimaryColor(accent);
            }
        }
    }

    /// <summary>Mixes the color toward white by <paramref name="amount" /> (0..1).</summary>
    private static System.Windows.Media.Color Shade(System.Windows.Media.Color c, double amount)
    {
        byte Mix(byte v) => (byte)(v + (255 - v) * amount);
        return System.Windows.Media.Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private void ModifyFontSize()
    {
        double size = CurrentFontSize;
        if (size < Global.MinFontSize)
        {
            return;
        }

        Application.Current.Resources["StdFontSize"] = size;
        Application.Current.Resources["StdFontSize1"] = size + 1;
        Application.Current.Resources["StdFontSize-1"] = size - 1;
    }

    public void ChangePrimaryColor(System.Windows.Media.Color color)
    {
        var theme = _paletteHelper.GetTheme();

        theme.PrimaryLight = new ColorPair(color.Lighten());
        theme.PrimaryMid = new ColorPair(color);
        theme.PrimaryDark = new ColorPair(color.Darken());

        _paletteHelper.SetTheme(theme);
    }

    public static void RegisterSystemColorSet(Config config, Action updateFunc)
    {
        SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if ((e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.General)
                && config.UiItem.CurrentTheme == nameof(ETheme.FollowSystem))
            {
                updateFunc?.Invoke();
            }
        };
    }
}
