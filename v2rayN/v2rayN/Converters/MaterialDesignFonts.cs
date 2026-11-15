using System.Windows.Media;

namespace v2rayN.Converters;

public class MaterialDesignFonts
{
    /// <summary>Default UI font: the Windows 11 system font, Segoe UI on older systems.</summary>
    public const string DefaultFont = "Segoe UI Variable Text";

    public static FontFamily MyFont { get; }

    static MaterialDesignFonts()
    {
        try
        {
            var fontFamily = AppManager.Instance.Config.UiItem.CurrentFontFamily;
            if (fontFamily.IsNotEmpty())
            {
                if (IsInstalled(fontFamily))
                {
                    // A symbol font (picked by accident) would turn every label into pictograms.
                    if (IsTextFont(fontFamily))
                    {
                        MyFont = new FontFamily(fontFamily);
                    }
                }
                else
                {
                    // A font file placed in the app's fonts folder (upstream behaviour).
                    var fontPath = Utils.GetFontsPath();
                    MyFont = new FontFamily(new Uri(@$"file:///{fontPath}\"), $"./#{fontFamily}");
                }
            }
        }
        catch
        {
        }
        MyFont ??= new FontFamily(IsInstalled(DefaultFont) ? DefaultFont : "Segoe UI");
    }

    public static bool IsInstalled(string name) =>
        Fonts.SystemFontFamilies.Any(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The font has Latin and Cyrillic letters (not a symbol / icon font).</summary>
    public static bool IsTextFont(string name)
    {
        try
        {
            var family = Fonts.SystemFontFamilies.FirstOrDefault(f => f.Source.Equals(name, StringComparison.OrdinalIgnoreCase));
            var typeface = family?.GetTypefaces().FirstOrDefault();
            if (typeface == null || !typeface.TryGetGlyphTypeface(out var glyphs) || glyphs.Symbol)
            {
                return false;
            }
            return "AaZzЖжЯя".All(ch => glyphs.CharacterToGlyphMap.ContainsKey(ch));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Installed text font names for the picker, sorted, default first.</summary>
    public static List<string> InstalledFonts()
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().Where(IsTextFont).OrderBy(n => n).ToList();
        names.Remove(DefaultFont);
        names.Insert(0, DefaultFont);
        return names;
    }
}
