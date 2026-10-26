using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public static class AppearanceManager
{
    public static void Apply(AppearancePreferences preferences)
    {
        preferences.Validate(); var app = Application.Current!;
        var variant = preferences.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        app.Resources.Remove("FwAccent");
        var theme = SukiUI.SukiTheme.GetInstance(); theme.ChangeBaseTheme(variant);
        var accent = app.TryGetResource("FwAccent", variant, out var brush) && brush is ISolidColorBrush solid ? solid.Color : Colors.SlateGray;
        var secondary = Blend(accent, variant == ThemeVariant.Dark ? Colors.White : Colors.Black, .15);
        theme.ChangeColorTheme(new SukiUI.Models.SukiColorTheme("Work Tracker", accent, secondary));
        // Workspace controls use the same resolved palette as Suki, including on repeated edits within one base theme.
        app.Resources["FwAccent"] = new SolidColorBrush(accent);
        var canvas = app.TryGetResource("FwCanvas", variant, out var background) && background is ISolidColorBrush baseBrush ? baseBrush.Color : Colors.Black;
        app.Resources["FwAccentSoft"] = new SolidColorBrush(Blend(canvas, accent, .16));
        app.Resources["FwOnAccent"] = Foreground(accent);
    }
    public static Color Blend(Color background, Color foreground, double amount) => Color.FromRgb((byte)Math.Round(background.R * (1 - amount) + foreground.R * amount), (byte)Math.Round(background.G * (1 - amount) + foreground.G * amount), (byte)Math.Round(background.B * (1 - amount) + foreground.B * amount));
    public static IBrush Foreground(Color color)
    {
        static double Channel(byte value) { var v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        var luminance = .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        return luminance > .179 ? Brushes.Black : Brushes.White;
    }
}
