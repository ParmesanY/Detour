namespace v2rayN.Views;

public partial class ConnectionRulesView
{
    public ConnectionRulesView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
    }
}
