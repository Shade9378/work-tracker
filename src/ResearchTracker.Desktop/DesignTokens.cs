using Avalonia;
using Avalonia.Media;
namespace ResearchTracker.Desktop;
public static class DesignTokens
{
    public const int Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Radius = 3;
    public const double Caption = 11, Body = 12, Section = 14, Title = 16;
    public static IBrush Brush(string token) => Application.Current?.TryGetResource("Fw" + token, Application.Current.ActualThemeVariant, out var value) == true && value is IBrush brush ? brush : Brushes.Gray;
}
