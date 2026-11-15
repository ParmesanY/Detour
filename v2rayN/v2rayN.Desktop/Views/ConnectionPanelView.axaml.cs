namespace v2rayN.Desktop.Views;

public partial class ConnectionPanelView : UserControl
{
    public ConnectionPanelView()
    {
        InitializeComponent();
        // The kill switch is built on the Windows firewall.
        tglKillSwitch.IsVisible = Utils.IsWindows();
    }
}
