using System.Windows;

namespace v2rayN.Views;

public partial class DiagnosticsWindow
{
    private CancellationTokenSource? _cts;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        txtReport.Text = ServiceLib.Resx.AppText.DiagRunning;
        try
        {
            var token = _cts.Token;
            var report = await Task.Run(() => DiagnosticsReport.BuildAsync(AppManager.Instance.Config, token), token);
            txtReport.Text = report;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            txtReport.Text = ex.ToString();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        btnNetCheck.IsEnabled = btnRerun.IsEnabled = btnCopy.IsEnabled = btnSave.IsEnabled = !busy;
    }

    private async void BtnRerun_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async void BtnNetCheck_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        txtReport.Text = ServiceLib.Resx.AppText.NetCheckRunning;
        try
        {
            var token = _cts.Token;
            var progress = new Progress<string>(s => txtReport.Text = ServiceLib.Resx.AppText.NetCheckRunning + Environment.NewLine + s);
            txtReport.Text = await Task.Run(() => NetCheckReport.BuildAsync(AppManager.Instance.Config, progress, token), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            txtReport.Text = ex.ToString();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(txtReport.Text);
            NoticeManager.Instance.Enqueue(ServiceLib.Resx.AppText.DiagCopied);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Diagnostics", ex);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"detour-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text|*.txt",
        };
        if (dlg.ShowDialog(this) == true)
        {
            File.WriteAllText(dlg.FileName, txtReport.Text);
        }
    }
}
