using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string text && Color.TryParse(text, out var color) ? new SolidColorBrush(color) : Brushes.Gray;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class ProjectColors
{
    public static IBrush Marker(Project? project) => project == null ? DesignTokens.Brush("TextMuted") : new SolidColorBrush(Color.Parse(project.DisplayColor));
    public static Color Heading(Project? project) => AppearanceManager.Blend(Colors.White, project == null ? Colors.SlateGray : Color.Parse(project.DisplayColor), .32);
    public static IBrush HeadingBrush(Project? project) => new SolidColorBrush(Heading(project));
    public static IBrush HeadingText => new SolidColorBrush(Color.Parse("#18202A"));
}
