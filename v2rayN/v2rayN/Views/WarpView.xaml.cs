namespace v2rayN.Views;

public partial class WarpView
{
    public WarpView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
    }
}
