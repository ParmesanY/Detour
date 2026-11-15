using System.Windows;

namespace v2rayN.Views;

/// <summary>
/// First-run wizard: add a subscription (or a server link), then let zapret find a working strategy.
/// Shown once, when there are no servers yet; closing it in any way ends it.
/// </summary>
public partial class FirstRunWindow
{
    private readonly Config _config = AppManager.Instance.Config;
    private readonly Func<string, Task> _addLink;
    private bool _busy;
    private bool _restarting;

    public FirstRunWindow(Func<string, Task> addLink)
    {
        InitializeComponent();
        _addLink = addLink;

        btnPaste.Click += (_, _) => txtLink.Text = WindowsUtils.GetClipboardData()?.Trim() ?? string.Empty;
        btnAdd.Click += async (_, _) => await AddAsync();
        btnZapret.Click += async (_, _) => await ZapretAsync();
        btnDone.Click += (_, _) => Close();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                return;
            }
            if (!_restarting)
            {
                _config.FirstRunDone = true;
                _ = ConfigHandler.SaveConfig(_config);
            }
        };
    }

    /// <summary>Not shown yet and no servers, or interrupted by the restart as administrator.</summary>
    public static async Task<bool> ShouldShowAsync(Config config)
    {
        if (config.FirstRunDone == false)
        {
            return true;
        }
        if (config.FirstRunDone == true)
        {
            return false;
        }
        var hasServers = (await AppManager.Instance.ProfileItems(""))?.Count > 0;
        if (hasServers)
        {
            // An existing user updating from a version without the wizard.
            config.FirstRunDone = true;
            await ConfigHandler.SaveConfig(config);
        }
        return !hasServers;
    }

    private static async Task<int> ServerCountAsync() => (await AppManager.Instance.ProfileItems(""))?.Count ?? 0;

    private async Task RefreshAsync()
    {
        var count = await ServerCountAsync();
        icoStep1.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 0 && txtStep1Status.Text.IsNullOrEmpty())
        {
            txtStep1Status.Text = string.Format(AppText.WizAdded, count);
        }

        var installed = ZapretManager.Instance.IsInstalled;
        var admin = Utils.IsAdministrator();
        btnZapret.Content = admin ? AppText.WizZapretPick : AppText.WizZapretRestart;
        btnZapret.IsEnabled = installed && !_busy;
        if (!installed)
        {
            txtStep2Status.Text = AppText.WizZapretNotInstalled;
        }
        else if (!admin && txtStep2Status.Text.IsNullOrEmpty())
        {
            txtStep2Status.Text = AppText.WizZapretAdmin;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnAdd.IsEnabled = btnPaste.IsEnabled = btnDone.IsEnabled = !busy;
        btnZapret.IsEnabled = !busy && ZapretManager.Instance.IsInstalled;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task AddAsync()
    {
        var link = txtLink.Text.Trim();
        if (link.IsNullOrEmpty())
        {
            return;
        }
        SetBusy(true);
        txtStep1Status.Text = AppText.WizAdding;
        try
        {
            var before = await ServerCountAsync();
            await _addLink(link);
            var after = await ServerCountAsync();
            txtStep1Status.Text = after > before ? string.Format(AppText.WizAdded, after) : AppText.WizAddFailed;
            if (after > before)
            {
                txtLink.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("FirstRun", ex);
            txtStep1Status.Text = AppText.WizAddFailed;
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private async Task ZapretAsync()
    {
        _config.ZapretItem.Enabled = true;
        if (!Utils.IsAdministrator())
        {
            // Continue here after the restart: zapret on + wizard in progress.
            _config.FirstRunDone = false;
            await ConfigHandler.SaveConfig(_config);
            _restarting = true;
            Close();
            await AppManager.Instance.RebootAsAdmin();
            return;
        }

        SetBusy(true);
        try
        {
            await ConfigHandler.SaveConfig(_config);
            await ConnectionRulesManager.Instance.SyncZapretAsync(_config);
            var (best, _) = await Task.Run(() => ZapretManager.Instance.AutoSelectStrategyAsync(_config, true, m =>
            {
                Dispatcher.Invoke(() => txtStep2Status.Text = m);
                return Task.CompletedTask;
            }));
            var ok = best is { Passed: > 0 };
            txtStep2Status.Text = ok
                ? string.Format(AppText.ZapretAutoResult, best!.Strategy, best.Passed, best.Total)
                : AppText.ZapretAutoNothing;
            icoStep2.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("FirstRun", ex);
            txtStep2Status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }
}
