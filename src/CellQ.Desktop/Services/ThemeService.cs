using System.Windows;
using System.Windows.Media;

namespace CellQ.Desktop.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }
    public static event Action<bool>? ThemeChanged;
    public static void Apply(string theme)
    {
        bool dark = theme == "Dark";
        Set("TextBrush", dark ? "#F8FCF8" : "#001D50");
        Set("CanvasBrush", dark ? "#001D50" : "#E6E6E6");
        Set("SurfaceBrush", dark ? "#003378" : "#FFFFFF");
        Set("FieldBrush", dark ? "#001D50" : "#F8FCF8");
        Set("DividerBrush", dark ? "#98B5E5" : "#A6A6A6");
        Set("SecondaryTextBrush", dark ? "#E6E6E6" : "#555555");
        Set("LinkBrush", dark ? "#00ACC0" : "#0067BF");
        IsDark = dark;
        ThemeChanged?.Invoke(dark);
    }
    private static void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
