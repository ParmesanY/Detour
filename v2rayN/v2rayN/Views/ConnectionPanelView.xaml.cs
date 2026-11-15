namespace v2rayN.Views;

public partial class ConnectionPanelView
{
    public ConnectionPanelView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
    }
}
