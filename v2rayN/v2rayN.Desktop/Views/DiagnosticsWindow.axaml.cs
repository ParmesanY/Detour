using Avalonia.Platform.Storage;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class DiagnosticsWindow : Window
{
    private CancellationTokenSource? _cts;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts?.Cancel();
        btnRerun.Click += async (_, _) => await RunAsync();
        btnNetCheck.Click += async (_, _) => await NetCheckAsync();
        btnCopy.Click += async (_, _) =>
        {
            await AvaUtils.SetClipboardData(this, txtReport.Text ?? string.Empty);
            NoticeManager.Instance.Enqueue(AppText.DiagCopied);
        };
        btnSave.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"detour-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            });
            if (file?.TryGetLocalPath() is { } path)
            {
                await File.WriteAllTextAsync(path, txtReport.Text ?? string.Empty);
            }
        };
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        txtReport.Text = AppText.DiagRunning;
        try
        {
            var token = _cts.Token;
            txtReport.Text = await Task.Run(() => DiagnosticsReport.BuildAsync(AppManager.Instance.Config, token), token);
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

    private async Task NetCheckAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        txtReport.Text = AppText.NetCheckRunning;
        try
        {
            var token = _cts.Token;
            var progress = new Progress<string>(s => txtReport.Text = AppText.NetCheckRunning + Environment.NewLine + s);
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

    private void SetBusy(bool busy)
    {
        progress.IsVisible = busy;
        btnNetCheck.IsEnabled = btnRerun.IsEnabled = btnCopy.IsEnabled = btnSave.IsEnabled = !busy;
    }
}
