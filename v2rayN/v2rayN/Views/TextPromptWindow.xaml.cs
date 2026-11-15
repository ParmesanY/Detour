namespace v2rayN.Views;

/// <summary>Small Fluent dialog: a prompt with a text box (or just a question when <paramref name="withInput" /> is false).</summary>
public partial class TextPromptWindow
{
    public string Value => txtValue.Text;

    public TextPromptWindow(string prompt, string initial = "", bool withInput = true)
    {
        InitializeComponent();
        Title = Global.AppName;
        txtPrompt.Text = prompt;
        txtValue.Text = initial;
        txtValue.Visibility = withInput ? Visibility.Visible : Visibility.Collapsed;
        btnOk.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) =>
        {
            if (withInput)
            {
                txtValue.Focus();
                txtValue.SelectAll();
            }
        };
    }

    public static string? Ask(Window? owner, string prompt, string initial = "")
    {
        var w = new TextPromptWindow(prompt, initial) { Owner = owner };
        return w.ShowDialog() == true ? w.Value.Trim() : null;
    }

    public static bool Confirm(Window? owner, string question)
    {
        var w = new TextPromptWindow(question, withInput: false) { Owner = owner };
        return w.ShowDialog() == true;
    }
}
