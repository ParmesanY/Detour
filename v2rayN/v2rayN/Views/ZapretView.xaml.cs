namespace v2rayN.Views;

public partial class ZapretView
{
    public ZapretView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
    }
}
