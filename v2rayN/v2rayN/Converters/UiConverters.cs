using System.Globalization;
using System.Windows.Data;

namespace v2rayN.Converters;

/// <summary>true for a non-empty string; with parameter "invert" the opposite.</summary>
public class NotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var result = value is string s && s.Trim().Length > 0;
        return parameter as string == "invert" ? !result : result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Server name → country code from its flag emoji ("FI"), or null. Parameter "name" returns the name without the flag.</summary>
public class ServerNameConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (code, name) = ServerNameFormatter.Split(value as string);
        return parameter as string == "name" ? name : code;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Server name → flag image (Twemoji PNG embedded in Resources/Flags), or null when the name has
/// no flag or there is no image for it. Parameter "badge" instead returns Visible/Collapsed for the
/// text badge fallback: shown only when there is a flag but no image.
/// </summary>
public class ServerFlagConverter : IValueConverter
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static System.Windows.Media.ImageSource? GetFlag(string? code)
    {
        if (code.IsNullOrEmpty())
        {
            return null;
        }
        lock (_cache)
        {
            if (_cache.TryGetValue(code!, out var cached))
            {
                return cached;
            }
            System.Windows.Media.ImageSource? image = null;
            try
            {
                var uri = new Uri($"pack://application:,,,/Resources/Flags/{code!.ToLowerInvariant()}.png");
                if (Application.GetResourceStream(uri) != null)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = uri;
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;
                }
            }
            catch (IOException)
            {
                // No image for this code: the text badge is used.
            }
            _cache[code!] = image;
            return image;
        }
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var (code, _) = ServerNameFormatter.Split(value as string);
        var image = GetFlag(code);
        if (parameter as string == "badge")
        {
            return code != null && image == null ? Visibility.Visible : Visibility.Collapsed;
        }
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Two-way bool inversion that also works with bool? targets (RadioButton.IsChecked).</summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>bool → Visibility; parameter "invert" shows on false.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert")
        {
            b = !b;
        }
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
