using System.Windows;
using ServiceLib.Resx;

namespace v2rayN.Views;

/// <summary>Detour Map: the opt-in, the connection type, and a measurement shown in full before it is sent.</summary>
public partial class BlockMapWindow
{
    private readonly Config _config = AppManager.Instance.Config;
    private CancellationTokenSource? _cts;
    private bool _loading = true;

    public BlockMapWindow()
    {
        InitializeComponent();
        var item = _config.BlockMapItem ??= new BlockMapItem();
        tglEnabled.IsChecked = item.Enabled;
        rbHome.IsChecked = item.Net != "mobile";
        rbMobile.IsChecked = item.Net == "mobile";
        _loading = false;

        ShowStatus();
        if (BlockMapManager.Instance.LastRun is { } last)
        {
            txtReport.Text = BlockMapManager.Describe(last);
        }
        BlockMapManager.Instance.StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            BlockMapManager.Instance.StateChanged -= OnStateChanged;
            _cts?.Cancel();
        };
    }

    private void OnStateChanged() => Dispatcher.InvokeAsync(ShowStatus);

    private void ShowStatus()
    {
        var item = _config.BlockMapItem;
        txtStatus.Text = item.LastSent is { } sent
            ? string.Format(AppText.BlockMapLastSent, sent.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))
            : AppText.BlockMapNeverSent;
        var busy = BlockMapManager.Instance.IsBusy;
        btnMeasure.IsEnabled = btnSend.IsEnabled = !busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TglEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.BlockMapItem.Enabled = tglEnabled.IsChecked == true;
        await ConfigHandler.SaveConfig(_config);
    }

    private async void Net_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _config.BlockMapItem.Net = rbMobile.IsChecked == true ? "mobile" : "home";
        await ConfigHandler.SaveConfig(_config);
    }

    private async void BtnMeasure_Click(object sender, RoutedEventArgs e) => await RunAsync(send: false);

    private async void BtnSend_Click(object sender, RoutedEventArgs e) => await RunAsync(send: true);

    private async Task RunAsync(bool send)
    {
        var pauseZapret = false;
        if (ZapretManager.Instance.IsRunning)
        {
            var answer = MessageBox.Show(this, AppText.BlockMapPauseZapretConfirm, "Detour",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
            pauseZapret = true;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        txtReport.Text = AppText.NetCheckRunning;
        var progressText = new Progress<string>(s => txtReport.Text = AppText.NetCheckRunning + Environment.NewLine + s);
        try
        {
            var run = await Task.Run(() => BlockMapManager.Instance.RunAsync(_config, send, pauseZapret, progressText, token), token);
            txtReport.Text = BlockMapManager.Describe(run);
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
            ShowStatus();
        }
    }

    private void BtnOpenSite_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart(BlockMapManager.Endpoint);
    }
}
