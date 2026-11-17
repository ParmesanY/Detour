namespace v2rayN.Views;

public partial class ConnectionPanelView
{
    public ConnectionPanelView()
    {
        InitializeComponent();
        this.WhenAnyValue(v => v.ViewModel).Subscribe(vm => DataContext = vm);
    }

    // Site check and block check live on the rules page: they edit the rules.
    private void BtnSiteCheck_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.ShowRulesPage();

    private void BtnBlockCheck_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.ShowRulesPage();

    private void BtnDiagnostics_Click(object sender, RoutedEventArgs e) => new DiagnosticsWindow { Owner = Window.GetWindow(this) }.Show();

    private void BtnBlockMap_Click(object sender, RoutedEventArgs e) => new BlockMapWindow { Owner = Window.GetWindow(this) }.Show();
}
