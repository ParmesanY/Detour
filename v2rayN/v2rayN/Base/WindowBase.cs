namespace v2rayN.Base;

public class WindowBase<TViewModel> : ReactiveWindow<TViewModel> where TViewModel : class
{
    public WindowBase()
    {
        Loaded += OnLoaded;
    }

    protected virtual void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sizeItem = ConfigHandler.GetWindowSizeItem(AppManager.Instance.Config, GetType().Name);
            if (sizeItem == null)
            {
                return;
            }

            Width = Math.Min(sizeItem.Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(sizeItem.Height, SystemParameters.WorkArea.Height);

            Left = SystemParameters.WorkArea.Left + ((SystemParameters.WorkArea.Width - Width) / 2);
            Top = SystemParameters.WorkArea.Top + ((SystemParameters.WorkArea.Height - Height) / 2);
        }
        catch { }
        finally
        {
            Dispatcher.BeginInvoke(FitContentWidth, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Older dialogs use fixed column widths; Fluent controls are a bit wider, which made content
    /// overflow and scroll sideways. Widen the window (within the screen) so nothing is cut off.
    /// </summary>
    private void FitContentWidth()
    {
        try
        {
            // The main window hosts wide tables that are meant to scroll.
            if (Application.Current?.MainWindow == this)
            {
                return;
            }
            var scroll = FindChild<System.Windows.Controls.ScrollViewer>(this);
            if (scroll == null || scroll.ExtentWidth <= scroll.ViewportWidth + 1)
            {
                return;
            }
            var extra = scroll.ExtentWidth - scroll.ViewportWidth + 24;
            var newWidth = Math.Min(Width + extra, SystemParameters.WorkArea.Width);
            Left = Math.Max(SystemParameters.WorkArea.Left, Left - (newWidth - Width) / 2);
            Width = newWidth;
            scroll.ScrollToHorizontalOffset(0);
        }
        catch { }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }
            var deeper = FindChild<T>(child);
            if (deeper != null)
            {
                return deeper;
            }
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        try
        {
            ConfigHandler.SaveWindowSizeItem(AppManager.Instance.Config, GetType().Name, Width, Height);
        }
        catch { }
    }
}
