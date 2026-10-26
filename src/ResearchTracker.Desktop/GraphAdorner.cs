using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;
public sealed class GraphAdorner : Control
{
    private GraphCanvas? editor;
    public GraphCanvas? Editor
    {
        get => editor;
        set { if (editor != null) editor.AdornerChanged -= InvalidateVisual; editor = value; if (editor != null) editor.AdornerChanged += InvalidateVisual; InvalidateVisual(); }
    }
    public GraphAdorner() { IsHitTestVisible = false; ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        base.Render(context); if (editor?.ViewModel is not { } vm) return;
        if (editor.SelectionRectangle is { } rectangle)
        {
            var fill = new SolidColorBrush(((ISolidColorBrush)DesignTokens.Brush("Accent")).Color) { Opacity = .08 };
            context.DrawRectangle(fill, new Pen(DesignTokens.Brush("Accent"), 1), rectangle);
        }
        foreach (var edge in vm.Edges)
        {
            var selected = vm.Selected == edge; var ends = editor.ArrowEnds(edge);
            foreach (var end in Enum.GetValues<EdgeEnd>())
            {
                var free = (end == EdgeEnd.Source ? edge.SourceId : edge.TargetId) == null;
                if (!selected && !free) continue;
                var point = editor.GraphToScreen(end == EdgeEnd.Source ? ends.Source : ends.Target);
                var brush = DesignTokens.Brush(selected ? "Accent" : "Warning"); var radius = selected ? 6 : 3;
                context.DrawEllipse(DesignTokens.Brush("Canvas"), new Pen(brush, selected ? 1.5 : 1), point, radius, radius);
                if (selected && editor.FocusedEnd == end) context.DrawEllipse(brush, null, point, 2.5, 2.5);
                if (selected)
                {
                    var text = new FormattedText(end == EdgeEnd.Source ? "Start" : "End", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, brush);
                    var position = point + new Vector(10, -20); context.DrawRectangle(DesignTokens.Brush("Canvas"), null, new Rect(position, new Size(text.Width + 4, text.Height + 2)), 2, 2); context.DrawText(text, position);
                }
            }
        }
    }
}
