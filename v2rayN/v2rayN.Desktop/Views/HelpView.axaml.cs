using ServiceLib.Services;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>Help, version, update and diagnostics. On macOS/Linux an update opens the release page.</summary>
public partial class HelpView : UserControl
{
    private AppUpdateManager.Release? _release;
    private bool _busy;

    public HelpView()
    {
        InitializeComponent();
        txtVersion.Text = $"Detour {AppUpdateManager.CurrentVersion}";
        btnUpdate.Click += async (_, _) => await UpdateAsync();
        btnDiagnostics.Click += (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var window = new DiagnosticsWindow();
            if (owner != null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                ShowState();
            }
        };
        ShowState();
    }

    private void ShowState()
    {
        if (_busy)
        {
            return;
        }
        _release ??= AppUpdateManager.Instance.Available;
        if (!AppUpdateManager.IsConfigured)
        {
            txtUpdateStatus.Text = AppText.UpdateNotConfigured;
            btnUpdate.IsEnabled = false;
            return;
        }
        btnUpdate.Content = _release == null ? AppText.UpdateCheck : string.Format(AppText.UpdateInstall, _release.Tag);
    }

    private async Task UpdateAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        btnUpdate.IsEnabled = false;
        try
        {
            using var ds = DownloadService.CreateDownloadServiceWithFallback();
            if (_release == null)
            {
                txtUpdateStatus.Text = AppText.UpdateChecking;
                _release = await AppUpdateManager.Instance.CheckAsync(ds);
                AppUpdateManager.Instance.Available = _release;
                txtUpdateStatus.Text = _release == null ? AppText.UpdateLatest : "";
                return;
            }

            if (!Utils.IsWindows())
            {
                // App bundles and packages are replaced by the user: open the release page.
                ProcUtils.ProcessStart($"https://github.com/{AppUpdateManager.Repo}/releases/tag/{_release.Tag}");
                return;
            }
            if (await UI.ShowYesNo(string.Format(AppText.UpdateConfirm, _release.Tag)) != ButtonResult.Yes)
            {
                return;
            }
            var error = await AppUpdateManager.Instance.InstallAndRestartAsync(ds, _release,
                msg => Dispatcher.UIThread.InvokeAsync(() => txtUpdateStatus.Text = msg).GetTask());
            if (error != null)
            {
                txtUpdateStatus.Text = string.Format(AppText.UpdateFailed, error);
            }
        }
        catch (Exception ex)
        {
            txtUpdateStatus.Text = string.Format(AppText.UpdateFailed, ex.Message);
        }
        finally
        {
            _busy = false;
            btnUpdate.IsEnabled = true;
            ShowState();
        }
    }
}
