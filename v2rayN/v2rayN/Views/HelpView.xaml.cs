using System.Windows;
using ServiceLib.Resx;
using ServiceLib.Services;

namespace v2rayN.Views;

public partial class HelpView
{
    private AppUpdateManager.Release? _release;
    private bool _busy;

    public HelpView()
    {
        InitializeComponent();
        txtVersion.Text = $"Detour {AppUpdateManager.CurrentVersion}";
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
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

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
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

            var answer = MessageBox.Show(Window.GetWindow(this)!, string.Format(AppText.UpdateConfirm, _release.Tag),
                "Detour", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
            var error = await AppUpdateManager.Instance.InstallAndRestartAsync(ds, _release,
                msg => Application.Current.Dispatcher.InvokeAsync(() => txtUpdateStatus.Text = msg).Task);
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

    private void BtnBlockMap_Click(object sender, RoutedEventArgs e)
    {
        new BlockMapWindow { Owner = Window.GetWindow(this) }.Show();
    }

    private void BtnTour_Click(object sender, RoutedEventArgs e)
    {
        new TourWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void BtnDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        new DiagnosticsWindow { Owner = Window.GetWindow(this) }.Show();
    }
}
