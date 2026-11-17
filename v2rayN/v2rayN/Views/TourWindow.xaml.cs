using System.Windows;

namespace v2rayN.Views;

/// <summary>Short first-run tour (TourContent): one page per step, Back / Next.</summary>
public partial class TourWindow
{
    private int _index;

    public TourWindow()
    {
        InitializeComponent();
        Show(0);
    }

    private void Show(int index)
    {
        var steps = TourContent.Steps;
        _index = Math.Clamp(index, 0, steps.Count - 1);
        txtCounter.Text = $"{_index + 1} / {steps.Count}";
        txtTitle.Text = steps[_index].Title;
        txtBody.Text = steps[_index].Text;
        btnBack.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        var last = _index == steps.Count - 1;
        btnNext.Content = last ? TourContent.Done : TourContent.Next;
        btnSkip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= TourContent.Steps.Count - 1)
        {
            Close();
            return;
        }
        Show(_index + 1);
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
}
