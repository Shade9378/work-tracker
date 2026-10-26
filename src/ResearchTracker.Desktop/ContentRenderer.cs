using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;
public static partial class ContentRenderer
{
    [GeneratedRegex(@"(\*\*[^*]+\*\*|\*[^*]+\*)")] private static partial Regex RichRuns();
    public static void Render(StackPanel panel, ContentBlock block)
    {
        if (block.Kind == BlockKind.Text)
        {
            foreach (var line in block.Text.Split('\n'))
            {
                var heading = line.StartsWith("# "); var text = heading ? line[2..] : line;
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = heading ? DesignTokens.Title : DesignTokens.Body, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(0, 3) };
                foreach (var piece in RichRuns().Split(text))
                {
                    var run = new Run(piece);
                    if (piece.StartsWith("**") && piece.EndsWith("**") && piece.Length >= 4) { run.Text = piece[2..^2]; run.FontWeight = FontWeight.Bold; }
                    else if (piece.StartsWith('*') && piece.EndsWith('*') && piece.Length >= 2) { run.Text = piece[1..^1]; run.FontStyle = FontStyle.Italic; }
                    tb.Inlines!.Add(run);
                }
                panel.Children.Add(tb);
            }
        }
    }
}
