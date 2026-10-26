using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

internal static class TaskDragSupport
{
    public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("work-tracker.task");
    public static bool TryRead(string? value, out Guid taskId, out DateTime? previousStart)
    {
        previousStart = null; var parts = (value ?? "").Split('|'); if (!Guid.TryParse(parts[0], out taskId)) return false;
        if (parts.Length > 1) { if (!long.TryParse(parts[1], out var ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false; previousStart = new DateTime(ticks); }
        return true;
    }
    public static void Attach(Control control, MainViewModel vm, WorkTask task, Action? click = null, DateTime? occurrence = null)
    {
        Point? origin = null; bool started = false; PointerPressedEventArgs? pressed = null;
        control.Focusable = true;
        control.PointerPressed += (_, e) => { if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any()) return; if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) { control.Focus(); origin = e.GetPosition(control); started = false; pressed = e; vm.Selected = task; } };
        control.PointerMoved += async (_, e) =>
        {
            if (origin == null || started || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(control).X - origin.Value.X) + Math.Abs(e.GetPosition(control).Y - origin.Value.Y) < 6 || task.Status is WorkStatus.Done or WorkStatus.Cancelled) return;
            started = true; using var data = new DataTransfer(); data.Add(DataTransferItem.Create(Format, task.Id + (occurrence is { } start ? "|" + start.Ticks : ""))); await DragDrop.DoDragDropAsync(pressed!, data, DragDropEffects.Move); origin = null;
        };
        control.PointerReleased += (_, e) => { var activate = origin is { } point && !started && Math.Abs(e.GetPosition(control).X - point.X) + Math.Abs(e.GetPosition(control).Y - point.Y) < 6; origin = null; if (activate) click?.Invoke(); };
        control.KeyDown += (_, e) => { if (control.IsFocused && click != null && e.Key is Key.Enter or Key.Space) { e.Handled = true; click(); } };
    }
}
