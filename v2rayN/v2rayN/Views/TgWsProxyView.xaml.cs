namespace v2rayN.Views;

public partial class TgWsProxyView
{
    public TgWsProxyView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
        btnCopyLink.Click += async (_, _) =>
        {
            var config = AppManager.Instance.Config;
            await TgWsProxyManager.EnsureSecretAsync(config);
            var link = TgWsProxyManager.GetLink(config);
            if (link is null)
            {
                return;
            }
            WindowsUtils.SetClipboardData(link);
            NoticeManager.Instance.Enqueue(AppText.TgLinkCopied);
        };
    }
}
